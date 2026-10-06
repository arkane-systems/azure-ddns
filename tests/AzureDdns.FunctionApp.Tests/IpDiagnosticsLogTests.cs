#region header

// AzureDdns.FunctionApp.Tests - IpDiagnosticsLogTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using System.Net;

using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class IpDiagnosticsLogTests
{
  #region Nested type: CapturingLogger

  private sealed class CapturingLogger : ILogger
  {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState> (TState state) where TState : notnull => null;

    public bool IsEnabled (LogLevel logLevel) => true;

    public void Log<TState> (LogLevel                          logLevel,
                             EventId                           eventId,
                             TState                            state,
                             Exception?                        exception,
                             Func<TState, Exception?, string>  formatter)
    {
      // Render structured values too: the {@Headers} dictionary is only visible via the state pairs.
      string rendered = formatter (arg1: state, arg2: exception);

      if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        rendered += " | " + string.Join (separator: "; ", values: pairs.Select (pair => $"{pair.Key}={FormatValue (pair.Value)}"));

      this.Entries.Add ((logLevel, rendered));
    }

    private static string FormatValue (object? value)
      => value is IDictionary<string, string> dictionary
           ? string.Join (separator: ",", values: dictionary.Select (pair => $"{pair.Key}:{pair.Value}"))
           : value?.ToString () ?? string.Empty;
  }

  #endregion

  private static HttpRequest CreateRequest (string remoteIp)
  {
    var context = new DefaultHttpContext ();
    context.Connection.RemoteIpAddress = IPAddress.Parse (remoteIp);
    context.Request.Headers["Authorization"] = "Basic c2VjcmV0OnNlY3JldA==";
    context.Request.Headers["User-Agent"]    = "test-agent";

    return context.Request;
  }

  [Fact]
  public void LogResolution_LogsSummaryOnly_WhenAllHeadersDisabled ()
  {
    var                logger     = new CapturingLogger ();
    HttpRequest        request    = CreateRequest ("203.0.113.10");
    IpResolutionResult resolution = new IpResolver ().Resolve (request: request, explicitIp: null);

    IpDiagnosticsLog.LogResolution (logger: logger, target: "home.example.com", request: request, resolution: resolution, logAllHeaders: false);

    Assert.Single (logger.Entries);
    Assert.Contains (expectedSubstring: "IP resolution diagnostics for home.example.com", actualString: logger.Entries[0].Message);
  }

  [Fact]
  public void LogResolution_LogsAllHeadersWithRedaction_WhenEnabled ()
  {
    var                logger     = new CapturingLogger ();
    HttpRequest        request    = CreateRequest ("203.0.113.10");
    IpResolutionResult resolution = new IpResolver ().Resolve (request: request, explicitIp: null);

    IpDiagnosticsLog.LogResolution (logger: logger, target: "home.example.com", request: request, resolution: resolution, logAllHeaders: true);

    string headerEntry = Assert.Single (collection: logger.Entries, predicate: entry => entry.Message.StartsWith ("Full request header diagnostics")).Message;
    Assert.Contains (expectedSubstring: "User-Agent:test-agent",   actualString: headerEntry);
    Assert.Contains (expectedSubstring: "Authorization:<redacted>", actualString: headerEntry);
    Assert.DoesNotContain (expectedSubstring: "c2VjcmV0", actualString: headerEntry);
  }

  [Fact]
  public void LogResolution_WarnsOnLoopbackSourceIp ()
  {
    var                logger     = new CapturingLogger ();
    HttpRequest        request    = CreateRequest ("127.0.0.1");
    IpResolutionResult resolution = new IpResolver ().Resolve (request: request, explicitIp: null);

    IpDiagnosticsLog.LogResolution (logger: logger, target: "home.example.com", request: request, resolution: resolution, logAllHeaders: false);

    Assert.Contains (collection: logger.Entries,
                     filter: entry => entry.Level == LogLevel.Warning && entry.Message.Contains ("resolved to loopback"));
  }

  [Fact]
  public void LogExplicitIpMismatch_Warns_OnlyWhenAddressesDiffer ()
  {
    var         logger  = new CapturingLogger ();
    HttpRequest request = CreateRequest ("203.0.113.10");

    IpResolutionResult same = new IpResolver ().Resolve (request: request, explicitIp: "203.0.113.10");
    IpDiagnosticsLog.LogExplicitIpMismatch (logger: logger, client: "c", target: "home.example.com", resolution: same);
    Assert.Empty (logger.Entries);

    IpResolutionResult different = new IpResolver ().Resolve (request: request, explicitIp: "198.51.100.7");
    IpDiagnosticsLog.LogExplicitIpMismatch (logger: logger, client: "c", target: "home.example.com", resolution: different);
    Assert.Single (logger.Entries);
    Assert.Equal (expected: LogLevel.Warning, actual: logger.Entries[0].Level);
  }
}
