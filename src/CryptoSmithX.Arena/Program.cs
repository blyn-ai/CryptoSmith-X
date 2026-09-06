using System.IO.Compression;
using CryptoSmithX.Arena;
using CryptoSmithX.Arena.Data;
using CryptoSmithX.Arena.Live;
using CryptoSmithX.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Sentry: errors go to the shared cryptosmith-x project, tagged by component so hub/api/webapp/arena
// are separable in the UI. The DSN comes from the environment (Sentry__Dsn); with none set — local
// dev — the SDK stays disabled and silent. Errors only, no performance tracing.
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.Environment = builder.Environment.EnvironmentName;
        o.TracesSampleRate = 0;
    });
}

// TLS is terminated by an upstream proxy (traefik) that sets X-Forwarded-Proto. The block is the
// WebApp's, unchanged, because the proxy in front of both is the same one. Arena has no cookie to
// mark Secure; what it gets from this is that Request.Scheme is https, so every absolute URL the
// page emits about itself is https, and that the address in the log line is the visitor's rather
// than the proxy's — on the one surface where the visitor is anonymous and unknown to us. The proxy
// is not loopback inside the compose network, so trust it explicitly.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// arena_reader, not marketdata: the connection string on a public surface must not be able to
// write. The role, its grant list and the argument for both are migration 0025.
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));

// The clock is injected rather than read from DateTime.UtcNow, because every age on this surface is
// a subtraction against it and a test that cannot move it cannot check any of them.
builder.Services.AddSingleton(TimeProvider.System);

// One cache for the process: single-flight, about a second, failures evicted. Singleton is the whole
// point — a scoped one would be a cache of one request, which is not a cache.
builder.Services.AddSingleton<ArenaCache>();

// The live button's two halves, both singletons and both for the same reason as the cache: they are
// about the process, not about a request.
//
// LiveNotifier is deliberately NOT an AddHostedService, which is the one line where it differs from
// the admin console's copy of it. It opens its LISTEN connection when the first stream subscribes
// and closes it when the last one leaves, because most visitors to a public page never press the
// button, and a process that holds a database connection all night to hear about a market nobody is
// watching is paying for a reader who is not there. The argument in full is on the class.
builder.Services.AddSingleton<LiveNotifier>();
builder.Services.AddSingleton<LiveStreamGate>();

builder.Services.AddControllersWithViews();

// The hosting layer registers Data Protection whether or not anything uses it, and with nowhere
// durable to put keys it writes one into ~/.aspnet/DataProtection-Keys inside the container — a
// layer thrown away on the next deployment — and warns about it on every start. Nothing on this site
// is protected: no sign-in, no cookie, no antiforgery token — every page is the same page for every
// visitor, which is the same property the cache rests on. So say so, and let the keys live and die
// with the process. The WebApp mounts a volume instead because losing its keys logs every admin out.
//
// This line used to read `AddDataProtection().UseEphemeralDataProtectionProvider()`, which does not
// do it: that call replaces the provider and leaves the file-backed key ring and its startup hosted
// service in place, so the key file was written and the warning logged exactly as before — verified
// by running it. Pointing the key ring at a per-process repository is what actually stops the write.
// The argument in full, and the one thing this still does not silence, are on ProcessKeyRing.
builder.Services.AddDataProtection()
    .AddKeyManagementOptions(o => o.XmlRepository = new ProcessKeyRing());

// The page ships a large table and a design system, and every anonymous first visit downloads all of
// it: measured at production scale the front page alone was 1,091,395 bytes with no Content-Encoding
// at all, and the vendored chart library another 197,922. Nothing in front of the app compresses —
// neither traefik compose file adds a compress middleware — so it is done here.
//
// EnableForHttps, deliberately. The usual reason not to compress over TLS is BREACH, which needs a
// secret in the response body and an attacker-controlled string beside it; this surface has neither
// — no cookie, no token, no per-visitor content, the same bytes for everyone — and that is the same
// property stated two blocks above. It is registered AFTER UseForwardedHeaders below for a reason:
// TLS is terminated upstream, so Request.IsHttps is only true once the forwarded scheme has been
// read.
//
// Fastest, not Optimal or SmallestSize. This is CPU spent per request on a page anonymous callers
// can ask for at any rate they like, and on HTML this repetitive the last few percent of ratio costs
// several times the time. The published .br/.gz siblings in wwwroot are a separate matter: only
// MapStaticAssets serves those, and switching to it changes how every asset URL on the site is
// generated — this middleware compresses those files on the fly instead.
//
// text/event-stream is NOT in the default MIME list and must not be added: compressing the live
// stream would buffer the very thing whose whole point is arriving immediately.
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "arena"));
}

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
// Compose ordering makes this a formality, the check makes a mis-start loud instead of weird.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

// Arena lives under /arena, and the prefix is stripped HERE — in the app — and nowhere else.
// Traefik routes by path prefix with no StripPrefix middleware on purpose: two places removing the
// same prefix means every generated link is correct only while both agree, and the day someone
// reorders proxy middleware every URL on the site breaks at once, silently, in production only.
// One place that strips it is also the one place that puts it back into Url.Action.
//
// Before UseForwardedHeaders and everything else: routing, static files and link generation all
// read PathBase, so anything registered above this line would see the unstripped path.
app.UsePathBase("/arena");

app.UseForwardedHeaders();

// Before the static files it compresses and before routing, so it wraps the response body of
// everything below it. After UseForwardedHeaders, so EnableForHttps sees the visitor's scheme rather
// than the proxy's hop.
app.UseResponseCompression();

// wwwroot arrives with the design system (fonts, tokens, the candle library). Registered now so the
// pipeline order is settled once. The directory is tracked empty (.gitkeep) rather than left out:
// without it StaticFileMiddleware logs "The WebRootPath was not found" on every start, and a
// warning that is expected today is a warning nobody reads tomorrow.
app.UseStaticFiles();
app.UseRouting();

// The default route goes FIRST, and this is not cosmetic. The pair page is addressed by two bare
// segments — /arena/BTC/USD — and a "{baseFamily}/{quoteFamily}" route registered above this line
// matches /arena/Pairs/Index too, swallowing every conventional controller URL on the site into a
// lookup for a pair called "Pairs/Index". Add the pair route BELOW, never above.
app.MapControllerRoute("default", "{controller=Pairs}/{action=Index}/{id?}");

// The pair page, addressed by the two halves of the folded pair: /arena/BTC/USD. BELOW the default
// route, for the reason stated above it.
//
// The constraint is not decoration, and it is not only about load. Without it this route matches any
// two segments at all, so every stray request — /arena/.well-known/security.txt, a scanner's
// /arena/wp-admin/setup — becomes a database lookup for a pair by that name, run by an anonymous
// caller, at whatever rate they care to send.
//
// A DOT IS EXCLUDED, and that was found by running the site rather than by reading it. WebApplication
// puts routing at the top of the pipeline, and StaticFileMiddleware deliberately stands down once an
// endpoint has been selected — so a two-segment route that accepted dots matched /arena/ds/styles.css
// before the static file middleware ever looked, and the entire design system's stylesheet came back
// as the "pair not listed" page with a 404. Every token on the site was missing and the page rendered
// in Times. Moving UseStaticFiles earlier does not fix it, because routing is already ahead of it;
// the route has to stop claiming things that are not pairs.
//
// The character class is what an asset code can contain: 0006 canonicalises base assets, 0024's
// family codes are written by the admin console, and both are short alphanumerics — BTC, USDT,
// 1000PEPE — with no dots anywhere. Case is NOT folded here or anywhere downstream; 0024 says the
// comparison is exact and the case is significant.
//
// IT CLOSES AT \z RATHER THAN $, and the templates are verbatim strings so the backslash survives
// into the route. In .NET `$` also matches immediately before a trailing newline, so /arena/BTC%0A/USD
// cleared this constraint on a class that contains no newline. PairAddress carries the full account.
//
// One thing this leaves open, for whoever adds the next asset: an EXTENSIONLESS file placed directly
// in wwwroot/<one folder>/ would be shadowed the same way. Everything the site serves today is
// either at the root (arena.css, the two scripts) or three segments deep (ds/…, vendor/…), and
// neither shape can be mistaken for a pair.
//
// The length is a separate `maxlength` constraint rather than a `{0,15}` quantifier inside the
// regex, because a brace inside a route template IS a route parameter: written the obvious way this
// line fails the build (ASP0017), and with that analyzer off it would be a broken route found by a
// visitor instead of by the compiler.
//
// WHAT THIS CONSTRAINT DOES NOT DO: it does not protect the action. The default route above reaches
// the same action as /arena/Pairs/Pair?baseFamily=…, binding from the query string, with no
// constraint anywhere near it — so the argument three paragraphs up was true and unenforced until
// the same rule was also checked at the action. It lives in PairAddress, and the two are held to the
// same pattern by a test. What only a route can do is the paragraph above this one: stop the
// two-segment template from claiming /arena/ds/styles.css.
app.MapControllerRoute(
    "pair",
    @"{baseFamily:regex(^[A-Za-z0-9][A-Za-z0-9_-]*\z):maxlength(16)}/{quoteFamily:regex(^[A-Za-z0-9][A-Za-z0-9_-]*\z):maxlength(16)}",
    new { controller = "Pairs", action = "Pair" });

// The live stream for one pair: /arena/live/BTC/USD, the same page addressed as an event stream.
//
// Three segments, so it cannot collide with the two-segment pair route above in either direction —
// and a literal first segment beats a parameter in endpoint routing anyway. The constraints are the
// pair route's, character for character, and for the same reason: an anonymous caller must not be
// able to turn an arbitrary string into a database lookup. Here it matters more, not less, because
// what this endpoint hands out is a connection held open rather than a page and a goodbye — and for
// exactly that reason the rule is enforced in the action as well (PairAddress), because this action
// is reachable as /arena/Pairs/Live?baseFamily=… too, where no constraint applies. Verified against
// the running app: that address returned 200 and an open event stream on any string at all.
app.MapControllerRoute(
    "pair-live",
    @"live/{baseFamily:regex(^[A-Za-z0-9][A-Za-z0-9_-]*\z):maxlength(16)}/{quoteFamily:regex(^[A-Za-z0-9][A-Za-z0-9_-]*\z):maxlength(16)}",
    new { controller = "Pairs", action = "Live" });

await app.RunAsync();
