using Nexode.Core.Model;

namespace Nexode.Core.Tree;

/// <summary>Effective (inherited) values of an entry, with the entry each value came from.</summary>
public sealed record Effective
{
    public string? Host { get; init; }
    public Guid? HostFrom { get; init; }
    public int? Port { get; init; }
    public string? Url { get; init; }
    public CredentialMode CredentialMode { get; init; }
    public string? Username { get; init; }
    public string? Domain { get; init; }
    /// <summary>Entry that owns the password secret (own entry, ancestor or credential profile).</summary>
    public Guid? SecretOwner { get; init; }
    public Guid? CredentialFrom { get; init; }
    /// <summary>Effective environment id; null = none.</summary>
    public string? Environment { get; init; }
    public Guid? EnvironmentFrom { get; init; }
    public OsFamily Os { get; init; }
    public double? ColorHue { get; init; }
    public string Icon { get; init; } = "circle";

    public string? UserWithDomain => string.IsNullOrEmpty(Username) ? null
        : string.IsNullOrEmpty(Domain) ? Username : $@"{Domain}\{Username}";

    public string? Upn => string.IsNullOrEmpty(Username) ? null
        : string.IsNullOrEmpty(Domain) || Username.Contains('@') ? Username : $"{Username}@{Domain}";
}

/// <summary>Resolves folder inheritance: credentials, host, environment, OS, color and icon.</summary>
public sealed class Resolver(EntryTree tree, IReadOnlyDictionary<string, Category>? categories = null, IReadOnlyDictionary<string, EnvironmentDef>? environments = null)
{
    public EntryTree Tree { get; } = tree;

    public EnvironmentDef? EnvironmentOf(string? id) => id is not null && environments is not null && environments.TryGetValue(id, out var e) ? e : null;

    /// <summary>True when the environment id or its name starts with <paramref name="filter"/>.</summary>
    public bool EnvironmentMatches(string? id, string filter) =>
        id is not null && (id.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                           || EnvironmentOf(id) is { } e && (e.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) || e.Short.Equals(filter, StringComparison.OrdinalIgnoreCase)));

    public Effective Resolve(Entry entry)
    {
        var (host, hostFrom) = ResolveHost(entry);
        var (mode, user, domain, secret, credFrom) = ResolveCredentials(entry);
        var (env, envFrom) = ResolveEnvironment(entry);
        var os = ResolveOs(entry);

        return new Effective
        {
            Host = host,
            HostFrom = hostFrom,
            Port = entry.Port ?? EntryKinds.DefaultPort(entry.Kind),
            Url = entry.Kind == EntryKind.Web ? entry.Url ?? (host is null ? null : $"https://{host}") : null,
            CredentialMode = mode,
            Username = user,
            Domain = domain,
            SecretOwner = secret,
            CredentialFrom = credFrom,
            Environment = env,
            EnvironmentFrom = envFrom,
            Os = os,
            ColorHue = ResolveColor(entry),
            Icon = ResolveIcon(entry, os)
        };
    }

    private (string?, Guid?) ResolveHost(Entry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Host)) return (entry.Host, entry.Id);
        foreach (var a in Tree.Ancestors(entry))
        {
            if (a.Kind == EntryKind.Host && !string.IsNullOrWhiteSpace(a.Host)) return (a.Host, a.Id);
            if (a.Kind == EntryKind.Folder) break;
        }

        return (null, null);
    }

    private (CredentialMode, string?, string?, Guid?, Guid?) ResolveCredentials(Entry entry)
    {
        var current = entry;
        var guard = 0;
        while (current is not null && guard++ < 1000)
        {
            switch (current.CredentialMode)
            {
                case CredentialMode.Own:
                    return (CredentialMode.Own, current.Username, current.Domain, current.Id, current.Id);
                case CredentialMode.Profile when Tree.Get(current.CredentialId) is { } profile:
                    return (CredentialMode.Profile, profile.Username, profile.Domain, profile.Id, profile.Id);
                case CredentialMode.Prompt:
                    return (CredentialMode.Prompt, current.Username, current.Domain, null, current.Id);
                case CredentialMode.None:
                    return (CredentialMode.None, null, null, null, current.Id);
            }

            // Credential profiles carry their own values.
            if (current.Kind == EntryKind.Credential)
            {
                return (CredentialMode.Own, current.Username, current.Domain, current.Id, current.Id);
            }

            current = Tree.Parent(current);
        }

        return (CredentialMode.None, null, null, null, null);
    }

    private (string?, Guid?) ResolveEnvironment(Entry entry)
    {
        if (entry.EnvironmentId is { } own) return (own.Length == 0 ? null : own, entry.Id);
        foreach (var a in Tree.Ancestors(entry))
        {
            if (a.EnvironmentId is { } inherited) return (inherited.Length == 0 ? null : inherited, a.Id);
        }

        return (null, null);
    }

    private OsFamily ResolveOs(Entry entry)
    {
        if (entry.Os != OsFamily.Unknown) return entry.Os;
        foreach (var a in Tree.Ancestors(entry))
        {
            if (a.Kind == EntryKind.Host && a.Os != OsFamily.Unknown) return a.Os;
            if (a.Kind == EntryKind.Folder) break;
        }

        return OsFamily.Unknown;
    }

    private double? ResolveColor(Entry entry)
    {
        if (entry.ColorHue is { } own) return own;
        if (entry.CategoryId is { } cat && categories?.GetValueOrDefault(cat)?.ColorHue is { } catHue) return catHue;
        foreach (var a in Tree.Ancestors(entry))
        {
            if (a.ColorHue is { } inherited) return inherited;
        }

        return null;
    }

    /// <summary>Own icon → OS (hosts) → category → kind.</summary>
    public string ResolveIcon(Entry entry, OsFamily os)
    {
        if (!string.IsNullOrEmpty(entry.Icon)) return entry.Icon;
        if (entry.Kind == EntryKind.Host && os != OsFamily.Unknown) return EntryKinds.OsIcon(os);
        if (entry.Kind == EntryKind.Host && entry.CategoryId is { } c1 && categories?.GetValueOrDefault(c1) is { } cat1) return cat1.Icon;
        if (entry.Kind is not EntryKind.Folder && !EntryKinds.IsConnection(entry.Kind) && entry.CategoryId is { } c2
            && categories?.GetValueOrDefault(c2) is { } cat2) return cat2.Icon;
        return EntryKinds.Icon(entry.Kind);
    }
}
