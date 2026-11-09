namespace Meeza.Marketing.Shared.Models;

public sealed record AdAccount(
    string Platform,
    string AccountId,
    string? Name,
    string? Currency,
    string? TimeZone,
    string? Status,
    bool? IsManager = null);
