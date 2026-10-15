# Security policy

Bildverk processes untrusted images and URLs, so security reports are very welcome.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository.

Include the Bildverk version, your configuration (the `Bildverk` section of appsettings.json), and if
possible the image or URL that triggers the problem. We aim to answer within a few days and to ship a fix
quickly. You'll be credited in the release notes unless you prefer otherwise.

## Supported versions

Only the latest minor release gets security fixes.

## Scope

- **In scope:** the middleware, cache, URL signing, configuration validation, and how Bildverk calls libvips.
- **Codec bugs:** bugs inside libvips or its bundled codecs belong upstream (libvips, mozjpeg, libwebp, libheif and so on), but tell us too. We'll make sure a fixed `NetVips.Native` release is picked up promptly.

## What is already in place

See the "Security" section of the README. Every item there has a test in
`tests/Bildverk.Tests/SecurityTests.cs`, the processing path is fuzzed nightly (`bench/Fuzz`), and the code
is scanned by CodeQL and the .NET security analyzers.
