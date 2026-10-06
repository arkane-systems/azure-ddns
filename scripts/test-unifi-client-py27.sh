#!/bin/sh
# End-to-end test of unifi-client/arkane-ddns-client.py under real CPython 2.7.18 (the version on Unifi gateways),
# with fake `curl` and `ip` commands on PATH. Run it inside the python:2.7.18-slim container with the client directory
# mounted at /client, for example (WSLC; `docker run` takes the same arguments):
#
#   wslc run --rm \
#     --mount "type=bind,source=<repo>/unifi-client,target=/client,readonly" \
#     --mount "type=bind,source=<repo>/scripts,target=/scr,readonly" \
#     python:2.7.18-slim sh /scr/test-unifi-client-py27.sh
#
# On Windows under Git Bash, set MSYS_NO_POSIX_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*" so /client and /scr are not rewritten.
set -eu

python --version 2>&1

mkdir -p /fakebin /tmp/out
cat > /fakebin/curl <<'EOF'
#!/bin/sh
# Records how it was called, then answers like the DDNS API (or fails, if FAKE_CURL_REPLY says so).
echo "$@" >> /tmp/out/curl_args
cat >> /tmp/out/curl_stdin
echo >> /tmp/out/curl_stdin
echo "${FAKE_CURL_REPLY:-good 203.0.113.5}"
exit "${FAKE_CURL_EXIT:-0}"
EOF
cat > /fakebin/ip <<'EOF'
#!/bin/sh
cat <<'OUT'
2: eth1: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500
    inet 203.0.113.5/24 brd 203.0.113.255 scope global eth1
    inet6 2001:db8::5/64 scope global
    inet6 2a02:1234:5678::5/64 scope global
    inet6 fe80::1/64 scope link
OUT
EOF
chmod +x /fakebin/curl /fakebin/ip

cat > /tmp/test.conf <<'EOF'
[api]
endpoint = https://example.azurewebsites.net/
client = my-client
key = p@ss"w&rd\x
zone = example.com
record = home

[interface]
wan = eth1

[options]
enable_ipv4 = true
enable_ipv6 = true
debug = true
EOF

export PATH="/fakebin:$PATH"

echo "--- run 1: both families change, API says good"
python /client/arkane-ddns-client.py /tmp/test.conf
echo "exit=$?"
echo "curl argv:"; cat /tmp/out/curl_args
echo "curl stdin:"; cat /tmp/out/curl_stdin
echo "cache:"; cat /var/cache/arkane-ddns-client/cache.json; echo

grep -q 'hostname=home.example.com&myip=203.0.113.5' /tmp/out/curl_stdin
grep -q 'myip=2a02%3A1234%3A5678%3A%3A5' /tmp/out/curl_stdin
grep -q 'user = "my-client:p@ss\\"w&rd\\\\x"' /tmp/out/curl_stdin
! grep -q 'p@ss' /tmp/out/curl_args
grep -q -- '--proto =https' /tmp/out/curl_args
grep -q '"ipv4": "203.0.113.5"' /var/cache/arkane-ddns-client/cache.json

echo "--- run 2: nothing changed, no API call"
rm /tmp/out/curl_args /tmp/out/curl_stdin
python /client/arkane-ddns-client.py /tmp/test.conf
test ! -e /tmp/out/curl_args

echo "--- run 3: API says badauth, cache must not advance"
rm /var/cache/arkane-ddns-client/cache.json
FAKE_CURL_REPLY=badauth python /client/arkane-ddns-client.py /tmp/test.conf
cat /var/cache/arkane-ddns-client/cache.json; echo
! grep -q '203.0.113.5' /var/cache/arkane-ddns-client/cache.json

echo "--- run 4: curl exits non-zero (e.g. timeout) even though body looks ok"
rm /var/cache/arkane-ddns-client/cache.json
FAKE_CURL_EXIT=28 python /client/arkane-ddns-client.py /tmp/test.conf
! grep -q '203.0.113.5' /var/cache/arkane-ddns-client/cache.json

echo "ALL PY2 CHECKS PASSED"
