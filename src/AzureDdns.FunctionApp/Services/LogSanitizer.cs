#region header

// AzureDdns.FunctionApp - LogSanitizer.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 4:00 PM

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   Makes request-derived strings safe to write to a log.
/// </summary>
/// <remarks>
///   Anything that originated from a caller (headers, hostnames, client names, query values) must pass
///   through <see cref="Sanitize" /> before it is logged, otherwise a caller can forge log entries by embedding
///   line breaks or other control characters (log forging, CWE-117). CodeQL's <c>cs/log-forging</c> check runs
///   on pull requests and flags unsanitized values.
/// </remarks>
public static class LogSanitizer
{
  /// <summary>
  ///   Replaces every control character (including CR and LF) with an underscore so the value cannot start a
  ///   new, forged log line.
  /// </summary>
  /// <param name="value">The value to sanitize; <see langword="null" /> is returned unchanged.</param>
  public static string? Sanitize (string? value)
    => value is null
         ? null
         : string.Create (length: value.Length,
                          state: value,
                          action: static (span, source) =>
                                  {
                                    for (var index = 0; index < source.Length; index++)
                                      span[index] = char.IsControl (source[index]) ? '_' : source[index];
                                  });
}
