#region header

// AzureDdns.FunctionApp.Tests - LogSanitizerTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class LogSanitizerTests
{
  [Theory]
  [InlineData ("a\r\nFORGED", "a__FORGED")]
  [InlineData ("tab\there",   "tab_here")]
  [InlineData ("clean.example.com", "clean.example.com")]
  public void Sanitize_ReplacesControlCharacters (string input, string expected)
    => Assert.Equal (expected: expected, actual: LogSanitizer.Sanitize (input));

  [Fact]
  public void Sanitize_ReturnsNull_ForNull () => Assert.Null (LogSanitizer.Sanitize (null));
}
