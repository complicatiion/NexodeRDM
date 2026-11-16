namespace Nexode.Core.Model;

/// <summary>Technical type of an entry: decides which handler opens it.</summary>
public enum EntryKind
{
    Folder = 0,
    Host = 1,
    Rdp = 10,
    Ssh = 11,
    Sftp = 12,
    Ftp = 13,
    Vnc = 14,
    Telnet = 15,
    Serial = 16,
    Scp = 17,
    WebDav = 18,
    S3 = 19,
    Web = 20,
    /// <summary>PowerShell remoting (WinRM) or PowerShell over SSH, in an embedded terminal.</summary>
    PowerShell = 21,
    Command = 30,
    Database = 31,
    Credential = 40,
    Note = 50
}

/// <summary>Fixed environments of v0.0.1/0.0.2 files (now <see cref="EnvironmentDef"/>); only used to read old data.</summary>
public enum EnvironmentTag
{
    None = 0,
    Production = 1,
    Staging = 2,
    Test = 3,
    Development = 4
}

/// <summary>Where an entry takes its credentials from.</summary>
public enum CredentialMode
{
    Inherit = 0,
    Own = 1,
    Profile = 2,
    Prompt = 3,
    None = 4
}

public enum OsFamily
{
    Unknown = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 3,
    Unix = 4,
    Network = 5
}

public enum SortMode
{
    Manual = 0,
    Name = 1,
    Kind = 2,
    LastUsed = 3
}

public enum FtpEncryption
{
    None = 0,
    Explicit = 1,
    Implicit = 2
}
