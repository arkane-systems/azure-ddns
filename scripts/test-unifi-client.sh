#!/bin/sh
# End-to-end test of unifi-client (the client script and install.sh) against the software it runs with on Unifi gateways:
# CPython 2.7.18 and curl 7.74.0 (both as shipped in Debian bullseye), talking to a local HTTPS server that
# plays the DDNS API (DynDNS v2, HTTP Basic auth). Only `ip` is faked (to report a WAN address).
#
# Run it inside a debian:bullseye-slim container with the client directory mounted at /client and this
# directory at /scr. With Docker (as in CI):
#
#   docker run --rm -v "$PWD/unifi-client:/client:ro" -v "$PWD/scripts:/scr:ro" \
#     debian:bullseye-slim sh /scr/test-unifi-client.sh
#
# With WSLC on Windows under Git Bash (the env vars stop /client and /scr being rewritten to Windows paths):
#
#   MSYS_NO_POSIX_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' wslc run --rm \
#     --mount "type=bind,source=<repo>\unifi-client,target=/client,readonly" \
#     --mount "type=bind,source=<repo>\scripts,target=/scr,readonly" \
#     debian:bullseye-slim sh /scr/test-unifi-client.sh
#
# Bullseye is end-of-life, so its packages come from the pinned snapshot.debian.org archive (the commented-out
# lines Debian leaves in sources.list), not the live mirrors.
set -eu

# ---------------------------------------------------------------- environment
sed -i 's/^# deb/deb/; /^deb http:\/\/deb\./d' /etc/apt/sources.list
apt-get -o Acquire::Check-Valid-Until=false -o Acquire::Retries=3 update -qq >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
  python2.7 curl ca-certificates openssl >/dev/null
ln -sf /usr/bin/python2.7 /usr/local/bin/python

python --version 2>&1
curl --version | head -1

mkdir -p /fakebin /tmp/out
export PATH="/fakebin:$PATH"

cat > /fakebin/ip <<'EOF'
#!/bin/sh
cat <<'OUT'
2: eth1: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500
    inet 203.0.113.5/24 brd 203.0.113.255 scope global eth1
    inet6 fd12:3456:789a::1/64 scope global
    inet6 2a02:1234:5678:0:a1b2:c3d4:e5f6:1/64 scope global temporary dynamic
    inet6 2a02:1234:5678:0:1111:2222:3333:4/64 scope global deprecated dynamic
    inet6 2001:db8::5/64 scope global
    inet6 2a02:1234:5678::5/64 scope global dynamic mngtmpaddr
    inet6 fe80::1/64 scope link
OUT
EOF
chmod +x /fakebin/ip

# ---------------------------------------------------------------- local HTTPS DDNS API
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -keyout /tmp/key.pem -out /tmp/cert.pem \
  -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" >/dev/null 2>&1

cat > /tmp/server.py <<'EOF'
# Minimal stand-in for /api/nic/update: checks Basic auth, answers "good <myip>" or "badauth".
import BaseHTTPServer, base64, ssl, urlparse

CLIENT = 'my-client'
KEY = 'p@ss"w&rd\\x'
EXPECTED = 'Basic ' + base64.b64encode(CLIENT + ':' + KEY)


class Handler(BaseHTTPServer.BaseHTTPRequestHandler):
    def do_GET(self):
        url = urlparse.urlparse(self.path)
        query = urlparse.parse_qs(url.query)
        authorized = self.headers.get('Authorization') == EXPECTED
        with open('/tmp/out/server.log', 'a') as log:
            log.write('%s hostname=%s myip=%s auth=%s\n' % (
                url.path, query.get('hostname', ['-'])[0], query.get('myip', ['-'])[0],
                'ok' if authorized else 'BAD'))
        if url.path != '/api/nic/update':
            status, body = 404, 'nohost'
        elif not authorized:
            status, body = 401, 'badauth'
        else:
            status, body = 200, 'good ' + query.get('myip', ['-'])[0]
        self.send_response(status)
        self.send_header('Content-Type', 'text/plain')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


server = BaseHTTPServer.HTTPServer(('127.0.0.1', 8443), Handler)
server.socket = ssl.wrap_socket(server.socket, certfile='/tmp/cert.pem', keyfile='/tmp/key.pem', server_side=True)
server.serve_forever()
EOF

python /tmp/server.py &
SERVER_PID=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do
  CURL_CA_BUNDLE=/tmp/cert.pem curl -s -o /dev/null https://localhost:8443/ready && break
  sleep 1
done
: > /tmp/out/server.log

# ---------------------------------------------------------------- client configuration
write_config () {
  cat > /tmp/test.conf <<EOF
[api]
endpoint = https://localhost:8443/
client = my-client
key = $1
zone = example.com
record = home

[interface]
wan = eth1

[options]
enable_ipv4 = true
enable_ipv6 = true
debug = true
EOF
}
CACHE=/var/cache/arkane-ddns-client/cache.json
# curl trusts only our test certificate (the client has no option to disable verification, by design).
export CURL_CA_BUNDLE=/tmp/cert.pem

fail () { echo "FAIL: $1"; echo "--- server log"; cat /tmp/out/server.log; echo "--- cache"; cat "$CACHE" 2>/dev/null || true; exit 1; }

# ---------------------------------------------------------------- scenarios
echo "--- run 1: both families change; key has quote, ampersand, backslash"
write_config 'p@ss"w&rd\x'
python /client/arkane-ddns-client.py /tmp/test.conf
cat /tmp/out/server.log
grep -q '^/api/nic/update hostname=home.example.com myip=203.0.113.5 auth=ok$' /tmp/out/server.log || fail "IPv4 request"
grep -q '^/api/nic/update hostname=home.example.com myip=2a02:1234:5678::5 auth=ok$' /tmp/out/server.log || fail "IPv6 request"
# The stable ISP-assigned GUA must win over the ULA, temporary, deprecated and documentation addresses listed first.
grep -q 'myip=fd12\|myip=2a02:1234:5678:0:\|myip=2001:db8' /tmp/out/server.log && fail "published a ULA/temporary/deprecated/documentation address"
grep -q '"ipv4": "203.0.113.5"' "$CACHE" || fail "IPv4 not cached"
grep -q '"ipv6": "2a02:1234:5678::5"' "$CACHE" || fail "IPv6 not cached"

echo "--- run 2: nothing changed, no API call, cache file not rewritten"
: > /tmp/out/server.log
CACHE_STAMP_BEFORE=$(stat -c %y "$CACHE")
sleep 1
python /client/arkane-ddns-client.py /tmp/test.conf
test ! -s /tmp/out/server.log || fail "unexpected API call"
test "$(stat -c %y "$CACHE")" = "$CACHE_STAMP_BEFORE" || fail "cache was rewritten although nothing changed"

echo "--- run 2b: cache directory wiped (as after a Unifi update) -> recreated, address reported again"
rm -rf /var/cache/arkane-ddns-client
: > /tmp/out/server.log
python /client/arkane-ddns-client.py /tmp/test.conf
grep -q 'myip=203.0.113.5 auth=ok' /tmp/out/server.log || fail "no update after the cache was wiped"
test -f "$CACHE" || fail "cache file not recreated"
test "$(stat -c %a /var/cache/arkane-ddns-client)" = "700" || fail "cache directory not recreated with mode 700"

echo "--- run 3: wrong key -> badauth, cache must not advance"
rm "$CACHE"
: > /tmp/out/server.log
write_config 'wrong-key'
python /client/arkane-ddns-client.py /tmp/test.conf
grep -q 'auth=BAD' /tmp/out/server.log || fail "server did not see the bad key"
grep -q '203.0.113.5' "$CACHE" && fail "cache advanced after badauth"

echo "--- run 4: API unreachable -> curl fails, cache must not advance"
kill "$SERVER_PID"
rm "$CACHE"
write_config 'p@ss"w&rd\x'
python /client/arkane-ddns-client.py /tmp/test.conf
grep -q '203.0.113.5' "$CACHE" && fail "cache advanced after connection failure"

echo "--- install.sh refuses to run without /data (not a Unifi OS device)"
# (/data does not exist in this container yet)
if bash /client/install.sh /client >/tmp/out/install_nodata.log 2>&1; then fail "install.sh ran without /data"; fi
grep -q '/data does not exist' /tmp/out/install_nodata.log || fail "install.sh gave no explanation without /data"

echo "--- install.sh: fresh install lands in /data, units point there"
mkdir -p /data
printf '#!/bin/sh\nexit 0\n' > /fakebin/systemctl
chmod +x /fakebin/systemctl
bash /client/install.sh /client >/tmp/out/install_fresh.log 2>&1 || { cat /tmp/out/install_fresh.log; fail "fresh install failed"; }
test -f /data/arkane-ddns-client/arkane-ddns-client.py || fail "script not installed in /data"
test -f /data/arkane-ddns-client/arkane-ddns-client.conf || fail "config template not installed in /data"
test -f /data/arkane-ddns-client/README.md || fail "README not installed in /data"
test "$(stat -c %a /data/arkane-ddns-client)" = "700" || fail "install dir is not mode 700"
test "$(stat -c %a /data/arkane-ddns-client/arkane-ddns-client.conf)" = "600" || fail "config is not mode 600"
test -f /etc/systemd/system/arkane-ddns-client.service || fail "service unit missing"
test -f /etc/systemd/system/arkane-ddns-client.timer || fail "timer unit missing"
grep -q '^ExecStart=/usr/bin/env python /data/arkane-ddns-client/arkane-ddns-client.py /data/arkane-ddns-client/arkane-ddns-client.conf$' /etc/systemd/system/arkane-ddns-client.service || fail "service ExecStart does not use /data"
grep -q '^CacheDirectory=arkane-ddns-client$' /etc/systemd/system/arkane-ddns-client.service || fail "service does not declare CacheDirectory"
# Comments may mention /usr/local (to explain why it is not used); no active setting may.
if cat /etc/systemd/system/arkane-ddns-client.service /etc/systemd/system/arkane-ddns-client.timer | grep -v '^#' | grep -q '/usr/local'; then fail "a unit setting still references /usr/local"; fi
# The installed script runs from its new home (no config path given -> reports a config error and exits 1).
if python /data/arkane-ddns-client/arkane-ddns-client.py /nonexistent.conf; then fail "installed script should reject a missing config"; fi

echo "--- install.sh: upgrade migrates the old /usr/local config and removes the old files"
rm -rf /data/arkane-ddns-client
mkdir -p /usr/local/bin /usr/local/etc/arkane-ddns-client
printf '#!/bin/sh\n' > /usr/local/bin/arkane-ddns-client.py
printf '[api]\nclient = migrated-marker\n' > /usr/local/etc/arkane-ddns-client.conf
chmod 600 /usr/local/etc/arkane-ddns-client.conf
printf 'old docs\n' > /usr/local/etc/arkane-ddns-client/README.md
bash /client/install.sh /client >/tmp/out/install_upgrade.log 2>&1 || { cat /tmp/out/install_upgrade.log; fail "upgrade install failed"; }
grep -q 'migrated-marker' /data/arkane-ddns-client/arkane-ddns-client.conf || fail "old config was not migrated"
test "$(stat -c %a /data/arkane-ddns-client/arkane-ddns-client.conf)" = "600" || fail "migrated config is not mode 600"
test ! -e /usr/local/bin/arkane-ddns-client.py || fail "old script not removed"
test ! -e /usr/local/etc/arkane-ddns-client.conf || fail "old config not removed"
test ! -e /usr/local/etc/arkane-ddns-client || fail "old docs not removed"

echo "--- install.sh: re-running keeps an existing config untouched"
printf '[api]\nclient = kept-marker\n' > /data/arkane-ddns-client/arkane-ddns-client.conf
bash /client/install.sh /client >/tmp/out/install_rerun.log 2>&1 || fail "re-run failed"
grep -q 'kept-marker' /data/arkane-ddns-client/arkane-ddns-client.conf || fail "re-run overwrote the config"

echo "ALL UNIFI CLIENT CHECKS PASSED"
