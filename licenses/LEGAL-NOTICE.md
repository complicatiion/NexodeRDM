# Legal Notice

## Disclaimer

Nexode RDM is provided **"as is"**, without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose and non-infringement (see the MIT License in [LICENSE.md](LICENSE.md)).

To the extent permitted by applicable law, the author is not liable for any damages arising from the use of Nexode RDM, including lost or disclosed credentials, data loss, misconfigured systems or interrupted operation. Mandatory statutory liability (e.g. for intent or gross negligence, fraudulently concealed defects, or under product liability law) remains unaffected.

## Credentials

Nexode stores passwords encrypted (AES-256-GCM). Their protection depends on how the vault is secured:

- **Master password:** without it the stored passwords cannot be recovered. Keep it safe – there is no reset.
- **System protection (no master password):** on Windows the key is bound to your user account (DPAPI). On Linux and macOS (version 0.0.2) the key is kept in a file only your user can read – anyone with access to your files could decrypt the passwords. Use a master password there.
- **Exports** with passwords are encrypted with their own export password. Exports *without* passwords still reveal host names and the structure of your infrastructure.

## Operator responsibility

Nexode is a tool for administrators. The operator is responsible for:

- the systems, accounts and credentials managed with Nexode, and for complying with the security policies of the organization that owns them,
- keeping exports, backups and portable copies of the data sources safe,
- reviewing imported entries – especially *Command* entries, which run local programs and stay disabled until marked as reviewed,
- web panels opened in the browser and certificates accepted there,
- compliance with company policies and applicable data protection law (data sources may contain personal data such as user names).

## Contact

Sven Klocke · sksdesign | complicatiion · [sksdesign.de](https://sksdesign.de)
- **Connections:** you are responsible for the systems you connect to. Nexode shows server fingerprints and certificates before trusting them; accepting a changed key without checking it can expose your credentials. VNC, Telnet and FTP without TLS are unencrypted protocols.
