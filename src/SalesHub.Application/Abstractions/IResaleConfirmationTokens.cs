namespace SalesHub.Application.Abstractions;

/// <summary>
/// Short-lived server tokens for the duplicate-Program-CID resale flow
/// (docs/02): the 409 carries a token binding (seller, cid, prior sale);
/// resubmitting with it proves the employee saw the prior sale and
/// explicitly confirmed "canceled/resale".
/// </summary>
public interface IResaleConfirmationTokens
{
    string Issue(Guid sellerUserId, string cid, Guid priorSaleId);

    /// <summary>Null when invalid, expired, or bound to different facts.</summary>
    Guid? ValidatePriorSaleId(string token, Guid sellerUserId, string cid);
}
