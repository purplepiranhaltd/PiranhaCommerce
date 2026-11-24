# Security

PiranhaCommerce handles customer details, staff sign-in and payments, so we take security reports seriously.

## Reporting a problem

**Please do not open a public issue for a security problem.** Use GitHub's private reporting instead: on the repository page choose **Security**, then **Report a vulnerability**. That sends the report to the maintainers only.

Please include what you found, the steps to reproduce it, the version, and what an attacker could do with it. We will confirm we have it, tell you what we plan to do, and let you know when a fix is out. Please give us a reasonable time to fix it before you tell anyone else.

## What we are interested in

- Ways to read, change or delete data without the right permission, including between staff roles, customers and API keys
- Ways around staff sign-in or two-factor, or around the sign-in limits
- Payment, webhook or refund handling that can be tricked
- Injection or script problems in the admin, the sample shop or the API
- Secrets that end up in logs, responses or the audit trail

## Supported versions

PiranhaCommerce is in alpha. Security fixes go into the latest release only.
