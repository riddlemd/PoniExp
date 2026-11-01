# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for security problems.

Report privately through GitHub's private vulnerability reporting:
[**Report a vulnerability**](https://github.com/riddlemd/PoniExp/security/advisories/new)
(the *Security* tab → *Advisories* → *Report a vulnerability*).

Include what you found, how to reproduce it, and which version or commit you tested. You'll get a
reply in the advisory thread; fixes are coordinated there before anything is disclosed.

## Supported versions

PoniExp is in active development. Only the latest release and `main` get security fixes.

## Design

What the router, machines and agents can and can't do is written down in the threat model,
[`docs/security.md`](docs/security.md). A report that shows the code doesn't match it is exactly
what we want to hear about.
