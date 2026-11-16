# Privacy

Nexode RDM is **local-first**:

- **No telemetry, no account, no cloud.** Nexode sends no usage data anywhere.
- **No automatic network access.** Nexode connects only to the servers you open (RDP, SSH, SFTP, FTP/FTPS, VNC, Telnet). Web panels open in your default browser only when you open them; links on the About page open in your browser.
- **Trusted servers:** the fingerprints of SSH host keys and FTPS certificates you trust are stored in `known_hosts.json` in the data folder (no passwords). Remove them in *Settings → Security*.
- **No external resources in the app.** Fonts and icons are embedded. Sunrise/sunset for the automatic theme are calculated locally from coordinates you enter.

## Data stored by Nexode

| Location | Content |
| --- | --- |
| `settings.json` | interface settings (language, theme, accent, window, list of data sources, device id) |
| `sources/*.db` | data sources (SQLite): folders, hosts, connections, user names, domains, notes, categories – **passwords only encrypted** |
| `keys/*.key` | Linux/macOS without master password only: the file that protects the data key (readable by your user only) |
| `backups/<id>/*.db` | automatic daily backups of the data sources (number configurable) |

Data folder: `%AppData%\Nexode` (Windows), `~/.local/share/Nexode` (Linux), `~/Library/Application Support/Nexode` (macOS); portable mode: `data` next to the program. Uninstalling with the setup asks whether this folder should be deleted.

## Clipboard

Copied passwords are marked as sensitive (excluded from the Windows clipboard history and cloud clipboard; "concealed" hints on macOS and KDE) and are cleared after 30 seconds by default if they are still on the clipboard.
