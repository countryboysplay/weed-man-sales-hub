using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SalesHub.Contracts.Forms;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Forms (CLAUDE.md §10) and Resources (§11) on real PostgreSQL.</summary>
public class WaveThreeFormsResourcesTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _agentId;
    private const string Password = "wave3-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "f3-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static FormDefinition EmailRequestDefinition() => new([
        new FormSection("s1", "Request", [
            new FormField("cid", "CID", "SingleLine", Required: true),
            new FormField("quoteType", "Quote Type", "Dropdown", Required: true,
                Options: ["Program Quote", "Upsell Quote", "Other"]),
            new FormField("otherDetail", "What else?", "SingleLine",
                VisibleWhen: new FieldCondition("quoteType", "Other")),
            new FormField("lawnArea", "Lawn area (sq ft)", "Number"),
        ]),
    ]);

    [Fact]
    public async Task Native_forms_validate_answers_against_the_published_version()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/forms/native",
            new CreateNativeFormRequest("Follow-Up Request", EmailRequestDefinition(), Publish: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var formId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "f3-agent", Password);

        // Missing required dropdown → refused.
        var missing = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/forms/{formId}/submissions",
            new SubmitFormRequest(new Dictionary<string, string> { ["cid"] = "482193" }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // Dropdown outside the option list → refused.
        var badOption = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/forms/{formId}/submissions",
            new SubmitFormRequest(new Dictionary<string, string>
            {
                ["cid"] = "482193", ["quoteType"] = "Nonsense",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, badOption.StatusCode);

        // Hidden branch answers are cleared, not stored: otherDetail is only
        // visible when quoteType == Other.
        var ok = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/forms/{formId}/submissions",
            new SubmitFormRequest(new Dictionary<string, string>
            {
                ["cid"] = "482193",
                ["quoteType"] = "Program Quote",
                ["otherDetail"] = "should be dropped",
                ["lawnArea"] = "8500",
            }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var stored = await _factory.WithDbAsync(db => db.FormSubmissions.SingleAsync());
        Assert.DoesNotContain("should be dropped", stored.AnswersJson);
        Assert.Contains("8500", stored.AnswersJson);
    }

    [Fact]
    public async Task Published_edits_create_a_new_version_and_take_effect_immediately()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/forms/native",
            new CreateNativeFormRequest("Evolving Form", EmailRequestDefinition(), Publish: true));
        var formId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var updated = await AuthFlows.PatchWithCsrfAsync(_owner, $"/api/v1/forms/native/{formId}",
            new UpdateNativeFormRequest(null, new FormDefinition([
                new FormSection("s1", "Request", [
                    new FormField("cid", "CID", "SingleLine", Required: true),
                ]),
            ]), null));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        Assert.Equal(2, await _factory.WithDbAsync(db =>
            db.FormVersions.CountAsync(v => v.FormId == formId)));

        // New submissions answer the new version: quoteType no longer exists.
        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "f3-agent", Password);
        var ok = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/forms/{formId}/submissions",
            new SubmitFormRequest(new Dictionary<string, string>
            {
                ["cid"] = "111222", ["quoteType"] = "stale-field",
            }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var stored = await _factory.WithDbAsync(db => db.FormSubmissions.SingleAsync());
        Assert.DoesNotContain("stale-field", stored.AnswersJson);
    }

    [Fact]
    public async Task Drafts_are_management_only_and_agents_cannot_author()
    {
        _ = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/forms/native",
            new CreateNativeFormRequest("Hidden Draft", EmailRequestDefinition(), Publish: false));

        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "f3-agent", Password);
        var list = await agent.GetFromJsonAsync<List<FormListItem>>("/api/v1/forms/", AuthFlows.Json);
        Assert.Empty(list!);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/forms/native",
                new CreateNativeFormRequest("Rogue", EmailRequestDefinition()))).StatusCode);

        // Google links refuse non-https and appear for everyone once created.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/forms/google-link",
                new CreateGoogleLinkRequest("Bad", "http://forms.google.com/x"))).StatusCode);
        _ = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/forms/google-link",
            new CreateGoogleLinkRequest("Cancellation Request", "https://forms.gle/abc123"));
        var after = await agent.GetFromJsonAsync<List<FormListItem>>("/api/v1/forms/", AuthFlows.Json);
        Assert.Contains(after!, f => f.Type == "GoogleLink" && f.DisplayName == "Cancellation Request");
    }

    [Fact]
    public async Task Email_requests_notify_management_and_vanish_on_completion()
    {
        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "f3-agent", Password);

        var created = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/email-requests/",
            new CreateEmailRequestRequest("482193", "customer@example.com",
                "Program Quote", "8500 sq ft", "Full program"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Management sees the queue; the agent cannot.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/v1/email-requests/")).StatusCode);
        var queue = await _owner.GetFromJsonAsync<List<EmailRequestDto>>(
            "/api/v1/email-requests/", AuthFlows.Json);
        Assert.Single(queue!);

        // Completion notifies the submitter and the request disappears —
        // deliberately not archived.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(_owner,
                $"/api/v1/email-requests/{requestId}/complete", new { })).StatusCode);
        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == _agentId && n.Title == "Email request completed")));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.EmailRequests.CountAsync()));
    }

    [Fact]
    public async Task Resource_access_rules_agents_view_watermarked_pdfs_and_never_download()
    {
        // Upload a real PDF as management.
        var pdfBytes = MakePdf("Pricing 2026 — internal");
        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(pdfBytes);
        filePart.Headers.ContentType = new("application/pdf");
        content.Add(filePart, "file", "pricing-2026.pdf");
        content.Add(new StringContent("2026 Pricing"), "title");
        var csrf = await AuthFlows.GetCsrfTokenAsync(_owner);
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resources/upload")
        {
            Content = content,
        };
        upload.Headers.Add("X-CSRF-TOKEN", csrf);
        var uploaded = await _owner.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var resourceId = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "f3-agent", Password);

        // Agent: no download route at all (management policy), viewer OK and
        // the streamed copy carries the viewer watermark.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync($"/api/v1/resources/{resourceId}/download")).StatusCode);
        var view = await agent.GetAsync($"/api/v1/resources/{resourceId}/view");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("application/pdf", view.Content.Headers.ContentType!.MediaType);
        var viewedBytes = await view.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(pdfBytes.Length, viewedBytes.Length); // derived copy, not the original

        // Manager download: watermarked PDF + an audit row the Owner can see.
        var download = await _owner.GetAsync($"/api/v1/resources/{resourceId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var audit = await _owner.GetAsync("/api/v1/resource-download-audit");
        var auditBody = await audit.Content.ReadAsStringAsync();
        Assert.Contains("2026 Pricing", auditBody);
        Assert.Contains("\"watermarked\":true", auditBody);

        // Agents cannot see the Owner audit surface, cannot upload, cannot delete.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/v1/resource-download-audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.DeleteWithCsrfAsync(agent, $"/api/v1/resources/{resourceId}")).StatusCode);

        // Search and favorites work for everyone.
        var found = await agent.GetAsync("/api/v1/resources/search?q=pricing");
        Assert.Contains("2026 Pricing", await found.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(agent,
                $"/api/v1/resources/{resourceId}/favorite", new { })).StatusCode);
    }

    /// <summary>A tiny single-page PDF generated in-process.</summary>
    private static byte[] MakePdf(string text)
    {
        SalesHub.Infrastructure.Services.PdfSharpWatermarker.EnsureFontsRegistered();
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawString(text, new XFont("Liberation Sans", 14), XBrushes.Black,
                new XPoint(72, 72));
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
