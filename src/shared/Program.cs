using System.Runtime.InteropServices;
using CompressionDemo;

// probe (default): start the apps on loopback, request /orders, print the table.
// serve: run the default app on 0.0.0.0 (8080 HTTP, 8443 HTTPS) and the
//        EnableForHttps app on 8444, for curl or a browser from the host.
var mode = args.Length > 0 ? args[0] : "probe";

if (mode == "probe")
{
    return await Probe.RunAsync(args);
}

if (mode == "serve")
{
    var cert = Apps.SelfSignedLocalhost();
    var defaults = Apps.Defaults(k => { k.ListenAnyIP(8080); k.ListenAnyIP(8443, l => l.UseHttps(cert)); });
    var httpsOn = Apps.HttpsOn(k => k.ListenAnyIP(8444, l => l.UseHttps(cert)));
    Console.WriteLine($"serving on {RuntimeInformation.FrameworkDescription}: defaults on 8080 (http) and 8443 (https), EnableForHttps on 8444 (https)");
    await Task.WhenAll(defaults.RunAsync(), httpsOn.RunAsync());
    return 0;
}

Console.Error.WriteLine("usage: CompressionDemo [probe [--json <file>] | serve]");
return 2;
