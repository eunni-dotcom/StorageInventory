# Security policy

StorageInventory is designed to be **read-only** towards the folders it scans. A way to make it modify, open, follow
into, or write inside a scanned tree, to overwrite an existing file, or to launch something the user didn't choose
is a security bug, and so is any unexpected network activity. The intended guarantees and their known limits are
described in [docs/native-security-review.md](docs/native-security-review.md).

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | Yes |

## Reporting a vulnerability

**Please do not open a public issue for security problems.** Report them privately through GitHub:

1. Go to the repository's **Security** tab.
2. Choose **Report a vulnerability** (GitHub Private Vulnerability Reporting).

Please include:

- the StorageInventory version and Windows version;
- what you expected and what happened (for example, which file was changed, or which link was followed);
- the smallest steps or folder layout that reproduce it, using **synthetic** folder and file names.

Please don't attach real inventory reports: they contain your file names and folder structure.

You should get an acknowledgement within a week. A fix will be released as soon as practical, and you'll be credited
in the release notes unless you'd rather not be.

## Known limitations (not vulnerabilities)

These are documented and accepted for 1.0; see the security review for details:

- A folder that is **swapped for a junction or symbolic link during a scan**, between being listed and being entered,
  can still be followed in a very small time window. The consequence is limited to a read-only listing of the link
  target.
- Aliases such as `\\localhost\C$` that Windows cannot resolve up front are caught by a runtime tripwire, after the
  first report files have been created in the report folder.
- The release executable is **not code-signed**.
