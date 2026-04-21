[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$FunctionBaseUrl,

  [Parameter(Mandatory = $true)]
  [string]$ClientName,

  [Parameter()]
  [string]$ClientKey = $env:AZURE_DDNS_CLIENT_KEY,

  [Parameter(Mandatory = $true)]
  [string]$Zone,

  [Parameter(Mandatory = $true)]
  [string]$Name,

  [Parameter()]
  [int]$DnsTimeoutSeconds = 120,

  [Parameter()]
  [int]$DnsPollIntervalSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command -Name Resolve-DnsName -ErrorAction SilentlyContinue))
{
  throw "Resolve-DnsName is required for this smoke test."
}

if ([string]::IsNullOrWhiteSpace($ClientKey))
{
  throw "ClientKey was not provided. Pass -ClientKey or set AZURE_DDNS_CLIENT_KEY."
}

$Zone = $Zone.Trim().TrimEnd('.')
$Name = $Name.Trim()

if ([string]::IsNullOrWhiteSpace($Zone))
{
  throw "Zone must not be empty."
}

if ([string]::IsNullOrWhiteSpace($Name))
{
  throw "Name must not be empty."
}

function Get-DyndnsEndpointUrl
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl
  )

  $trimmed = $BaseUrl.Trim().TrimEnd('/')

  if ($trimmed.EndsWith('/api/nic/update', [System.StringComparison]::OrdinalIgnoreCase))
  {
    return $trimmed
  }

  return "$trimmed/api/nic/update"
}

function Get-TestFqdn
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$RecordName,

    [Parameter(Mandatory = $true)]
    [string]$DnsZone
  )

  if ($RecordName -eq '@')
  {
    return $DnsZone
  }

  return "$RecordName.$DnsZone"
}

function New-Rfc5737Ipv4
{
  $prefixes = @(
    '192.0.2',
    '198.51.100',
    '203.0.113'
  )

  $prefix = $prefixes | Get-Random
  $lastOctet = Get-Random -Minimum 1 -Maximum 255

  return "$prefix.$lastOctet"
}

function New-Rfc3849Ipv6
{
  $groups = 1..6 | ForEach-Object { "{0:x4}" -f (Get-Random -Minimum 0 -Maximum 65536) }
  return "2001:db8:{0}" -f ($groups -join ':')
}

function ConvertTo-CanonicalIpAddressString
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$IpAddress
  )

  return ([System.Net.IPAddress]::Parse($IpAddress)).ToString()
}

function New-QueryString
{
  param(
    [Parameter(Mandatory = $true)]
    [hashtable]$Parameters
  )

  return (($Parameters.GetEnumerator() | Sort-Object Key | ForEach-Object {
        "{0}={1}" -f [System.Uri]::EscapeDataString([string]$_.Key),
        [System.Uri]::EscapeDataString([string]$_.Value)
      }) -join '&')
}

function New-BasicAuthHeader
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password
  )

  $credentials = "$Username`:$Password"
  $base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($credentials))
  return "Basic $base64"
}

function Invoke-DynDnsUpdate
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$EndpointUrl,

    [Parameter(Mandatory = $true)]
    [string]$Client,

    [Parameter(Mandatory = $true)]
    [string]$Key,

    [Parameter(Mandatory = $true)]
    [string]$Hostname,

    [Parameter(Mandatory = $true)]
    [string]$IpAddress,

    [Parameter(Mandatory = $true)]
    [ValidateSet('A', 'AAAA')]
    [string]$ExpectedRecordType
  )

  $parsedIpAddress = $null
  if (-not [System.Net.IPAddress]::TryParse($IpAddress, [ref]$parsedIpAddress))
  {
    throw "IpAddress '$IpAddress' is not a valid IP address."
  }

  $actualRecordType = if ($parsedIpAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) { 'AAAA' } else { 'A' }
  if ($actualRecordType -ne $ExpectedRecordType)
  {
    throw "IpAddress '$IpAddress' does not match expected record type '$ExpectedRecordType'. Detected record type '$actualRecordType'."
  }
  $basicAuthHeader = New-BasicAuthHeader -Username $Client -Password $Key

  $query = New-QueryString @{
    hostname = $Hostname
    myip     = $IpAddress
  }

  $uri = "$EndpointUrl`?$query"
  $canonicalIpAddress = ConvertTo-CanonicalIpAddressString -IpAddress $IpAddress
  $expectedBody = "good $canonicalIpAddress"

  $response = Invoke-WebRequest -Uri $uri `
                                -Method GET `
                                -Headers @{ Authorization = $basicAuthHeader } `
                                -SkipHttpErrorCheck

  $body = ($response.Content | Out-String).Trim()

  if ($response.StatusCode -ne 200)
  {
    throw "Update request failed with HTTP $($response.StatusCode): $body"
  }

  if ($body -ne $expectedBody)
  {
    throw "Unexpected response body. Expected '$expectedBody' but got '$body'."
  }

  return $body
}

function Get-AuthoritativeNameServer
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$DnsZone
  )

  $servers = Resolve-DnsName -Name $DnsZone -Type NS -DnsOnly -NoHostsFile |
    Where-Object { $_.Type -eq 'NS' } |
    Select-Object -ExpandProperty NameHost -Unique |
    ForEach-Object { $_.TrimEnd('.') }

  if (-not $servers)
  {
    throw "No authoritative name servers were returned for zone '$DnsZone'."
  }

  return $servers | Get-Random
}

function Get-AuthoritativeIpValues
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$Server,

    [Parameter(Mandatory = $true)]
    [string]$Fqdn,

    [Parameter(Mandatory = $true)]
    [ValidateSet('A', 'AAAA')]
    [string]$RecordType
  )

  $results = Resolve-DnsName -Name $Fqdn `
                              -Type $RecordType `
                              -Server $Server `
                              -DnsOnly `
                              -NoHostsFile `
                              -QuickTimeout `
                              -ErrorAction SilentlyContinue

  if (-not $results)
  {
    return @()
  }

  return @(
    $results |
      Where-Object { $_.Type -eq $RecordType } |
      Select-Object -ExpandProperty IPAddress -Unique |
      ForEach-Object { ConvertTo-CanonicalIpAddressString -IpAddress $_ }
  )
}

function Wait-ForDnsValue
{
  param(
    [Parameter(Mandatory = $true)]
    [string]$Server,

    [Parameter(Mandatory = $true)]
    [string]$Fqdn,

    [Parameter(Mandatory = $true)]
    [ValidateSet('A', 'AAAA')]
    [string]$RecordType,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedIp,

    [Parameter(Mandatory = $true)]
    [int]$TimeoutSeconds,

    [Parameter(Mandatory = $true)]
    [int]$PollIntervalSeconds
  )

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $expectedCanonicalIp = ConvertTo-CanonicalIpAddressString -IpAddress $ExpectedIp

  do
  {
    $values = @(Get-AuthoritativeIpValues -Server $Server -Fqdn $Fqdn -RecordType $RecordType)

    if ($values.Count -eq 1 -and $values[0] -eq $expectedCanonicalIp)
    {
      return $values
    }

    Start-Sleep -Seconds $PollIntervalSeconds
  }
  while ((Get-Date) -lt $deadline)

  $finalValues = @(Get-AuthoritativeIpValues -Server $Server -Fqdn $Fqdn -RecordType $RecordType)
  $display = if ($finalValues.Count -eq 0) { '<none>' } else { $finalValues -join ', ' }

  throw "Timed out waiting for $RecordType $Fqdn on $Server to become '$expectedCanonicalIp'. Last value(s): $display"
}

$endpointUrl = Get-DyndnsEndpointUrl -BaseUrl $FunctionBaseUrl
$fqdn = Get-TestFqdn -RecordName $Name -DnsZone $Zone
$ipv4 = ConvertTo-CanonicalIpAddressString -IpAddress (New-Rfc5737Ipv4)
$ipv6 = ConvertTo-CanonicalIpAddressString -IpAddress (New-Rfc3849Ipv6)
$authoritativeServer = Get-AuthoritativeNameServer -DnsZone $Zone

Write-Host "DynDNS Smoke test target : $fqdn"
Write-Host "Function endpoint       : $endpointUrl"
Write-Host "Authoritative DNS       : $authoritativeServer"
Write-Host "IPv4 test value         : $ipv4"
Write-Host "IPv6 test value         : $ipv6"

Write-Host ""
Write-Host "Step 1/4: Updating A record via DynDNS..."
Invoke-DynDnsUpdate -EndpointUrl $endpointUrl `
                    -Client $ClientName `
                    -Key $ClientKey `
                    -Hostname $fqdn `
                    -IpAddress $ipv4 `
                    -ExpectedRecordType 'A' | Out-Null

Write-Host "Step 2/4: Verifying authoritative A record..."
Wait-ForDnsValue -Server $authoritativeServer `
                 -Fqdn $fqdn `
                 -RecordType 'A' `
                 -ExpectedIp $ipv4 `
                 -TimeoutSeconds $DnsTimeoutSeconds `
                 -PollIntervalSeconds $DnsPollIntervalSeconds | Out-Null

Write-Host "Step 3/4: Updating AAAA record via DynDNS..."
Invoke-DynDnsUpdate -EndpointUrl $endpointUrl `
                    -Client $ClientName `
                    -Key $ClientKey `
                    -Hostname $fqdn `
                    -IpAddress $ipv6 `
                    -ExpectedRecordType 'AAAA' | Out-Null

Write-Host "Step 4/4: Verifying authoritative AAAA record and A/AAAA independence..."
Wait-ForDnsValue -Server $authoritativeServer `
                 -Fqdn $fqdn `
                 -RecordType 'AAAA' `
                 -ExpectedIp $ipv6 `
                 -TimeoutSeconds $DnsTimeoutSeconds `
                 -PollIntervalSeconds $DnsPollIntervalSeconds | Out-Null

$aValuesAfterIpv6 = @(Get-AuthoritativeIpValues -Server $authoritativeServer -Fqdn $fqdn -RecordType 'A')

if ($aValuesAfterIpv6.Count -ne 1 -or $aValuesAfterIpv6[0] -ne $ipv4)
{
  $actual = if ($aValuesAfterIpv6.Count -eq 0) { '<none>' } else { $aValuesAfterIpv6 -join ', ' }
  throw "A/AAAA independence check failed. Expected A $fqdn to remain '$ipv4' but found '$actual'."
}

Write-Host ""
Write-Host "DynDNS Smoke test passed."
Write-Host "Verified A    : $fqdn -> $ipv4"
Write-Host "Verified AAAA : $fqdn -> $ipv6"
