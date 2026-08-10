namespace SalesHub.Api.Endpoints;

/// <summary>
/// ProblemDetails helpers carrying the standardized extensions (docs/02):
/// code and correlationId on every error.
/// </summary>
internal static class Problems
{
    internal static IResult Validation(HttpContext http, string detail, string code = "validation") =>
        Results.Problem(
            title: "The request is invalid.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            type: $"https://saleshub/problems/{code}",
            extensions: Extensions(http, code));

    internal static IResult Auth(HttpContext http, string code, string detail) =>
        Results.Problem(
            title: "Authentication failed.",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized,
            type: $"https://saleshub/problems/{code}",
            extensions: Extensions(http, code));

    internal static IResult Forbidden(HttpContext http, string detail, string code = "forbidden") =>
        Results.Problem(
            title: "Not allowed.",
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden,
            type: $"https://saleshub/problems/{code}",
            extensions: Extensions(http, code));

    internal static IResult NotFound(HttpContext http, string detail, string code = "notFound") =>
        Results.Problem(
            title: "Not found.",
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            type: $"https://saleshub/problems/{code}",
            extensions: Extensions(http, code));

    internal static IResult Conflict(HttpContext http, string detail, string code = "conflict") =>
        Results.Problem(
            title: "Conflict.",
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            type: $"https://saleshub/problems/{code}",
            extensions: Extensions(http, code));

    private static Dictionary<string, object?> Extensions(HttpContext http, string code) => new()
    {
        ["code"] = code,
        ["correlationId"] = http.TraceIdentifier,
    };
}
