namespace Leo.Core.Updates;

/// <summary>
/// Public keys whose <see cref="UpdateSignature"/> the updater accepts (LEO.md §26). Made once with
/// `dotnet run tools/update-key.cs -- new`; the matching private key lives only in the GitHub environment "release" (secret
/// LEO_UPDATE_KEY) and in the publisher's password manager. To replace a key, add the new one here first, ship a
/// release signed with the OLD key, then sign later releases with the new key and remove the old one.
/// `tools/update-key.cs sign` (release workflow) refuses a secret whose public key isn't listed here.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<string> Public =
    [
        // LEO update key 1, made 2026-11-24 with tools/update-key.cs (private key: GitHub secret LEO_UPDATE_KEY).
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmW5lLG/k8RrgyzKiZ7qOr+99w046dOWtyuXMAvvf68jvsRAueDRkiJbhLYGm9FdYGjC8rkJ1uZ0uLxjIpBjAGw==",
    ];
}
