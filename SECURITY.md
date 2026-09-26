# Security

Second Key handles recordings of other systems' traffic, which can contain personal data
and credentials. Please report a vulnerability privately through GitHub's
"Report a vulnerability" (Security → Advisories) on this repository rather than in an
issue.

What this repository promises about data:

- Capture redacts configured headers before anything is written to disk.
- Nothing is sent over the network except to the two systems under comparison.
- No recording from a real system is ever committed; the secret scan runs on every
  commit and in CI.
