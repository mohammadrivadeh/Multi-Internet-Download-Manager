# ADR 0008: Bounded redirects and final-resource identity

- Date: 2026-11-14
- Status: Accepted; Windows/TLS endpoint acceptance remains pending

## Redirect policy

Support bodyless GET redirects 301/302/303/307/308 explicitly in HttpTransferPolicy, with at most eight hops, normalized URI loop detection and a shared thirty-second header/chain deadline. Resolve relative Location values against the current URL. Accept HTTP(S) only, reject embedded credentials and prohibit every HTTPS-to-HTTP downgrade. Dispose intermediate responses and owned requests on success, failure or cancellation. Keep normal TLS verification and the originally selected transport/pool for every hop.

Redirect requests forward only Range, If-Range and Accept-Encoding, preserving request protocol/version policy. Authorization, Cookie and arbitrary custom headers are not forwarded, including same-origin redirects. Production transports keep automatic redirects, cookies and proxies disabled. Reject clients with any default request headers: HttpClient would otherwise reapply defaults at each hop outside the forwarding allowlist. Reject an unexpected automatically redirected response rather than silently accepting an unvalidated chain. Authentication/session forwarding remains a separate future design.

## Representation identity

Strong ETags are scoped to a resource URI, not globally unique file identifiers. Include the canonical final URI (without fragments) in the probed resource identity. Require the same final URI, length, ETag and range capability across selected paths. Before writing any range payload, verify its final URI matches the probed URI; sequential fallback also verifies the final URI before writing. Final reprobe must still match. Different CDN destinations with matching ETag/size are conservatively rejected; trusted mirror identity is not inferred.

For redirected jobs, checkpoint source identity hashes the original URL plus final canonical URL. No plaintext redirect URL is saved in checkpoint metadata. If the redirect target changes on resume, preserve the partial file and reject old metadata instead of mixing bytes. Direct URLs retain the previous source-hash formula, so existing nonredirected checkpoints remain compatible. A rotating signed target can therefore require a new destination; expired-link refresh is not implemented.

## Verification and boundaries

Tests cover each supported status, relative and cross-origin chains, protocol/range header preservation, credential removal, default-header rejection, missing targets, hop limits, loops, unsafe schemes/userinfo, HTTPS downgrades, cancellation and intermediate body disposal. Engine tests cover redirected ranged/sequential completion and refusal to mix final URIs across workers, paths or resume even when ETags match. Real ASP.NET HTTP smoke verifies a three-hop full download against the fixture hash and rejection of a loop before creating partial state.

The diagnostic NetworkProbe intentionally keeps reporting redirects without following them; it tests one destination/path response. The production engine and DownloadHarness use this new bounded policy. Fake HTTPS policy tests do not validate a real TLS handshake. Real Windows/TLS/CDN acceptance remains in PENDING-ACCEPTANCE.md.

A read-only attempt to query GitHub Actions via api.github.com returned HTTP 403 from this environment. No remote CI result is inferred; local build/test outcomes are recorded separately.
