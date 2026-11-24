#!/bin/bash
# http-get URL: fetches a plain http:// URL with bash alone, as the image carries no curl (curl's TLS, Kerberos and
# LDAP libraries were most of the image's known vulnerabilities). Prints the response body and exits 0 for a 2xx
# status, 1 for any other status or no answer, 2 for a URL it cannot fetch. The container's HEALTHCHECK runs it.
#
#   docker exec comicmaintainer http-get http://127.0.0.1:5000/health
set -u

url="${1:-}"
if [[ ! $url =~ ^http://([^/:?#]+)(:([0-9]+))?([/?][^#]*)?$ ]]; then
    echo "usage: http-get http://host[:port][/path] (plain http only)" >&2
    exit 2
fi
host="${BASH_REMATCH[1]}"
port="${BASH_REMATCH[3]:-80}"
path="${BASH_REMATCH[4]:-/}"
[[ $path == /* ]] || path="/$path"
authority="$host"
[[ -n ${BASH_REMATCH[3]} ]] && authority="$host:$port"

exec 3<>"/dev/tcp/$host/$port" || exit 1
# HTTP/1.0: the answer comes whole, never chunked, and the server closes the connection after it.
printf 'GET %s HTTP/1.0\r\nHost: %s\r\nUser-Agent: http-get\r\nConnection: close\r\n\r\n' "$path" "$authority" >&3

IFS= read -r -t 10 status <&3 || exit 1
status="${status%$'\r'}"
# The headers end at the first empty line.
while IFS= read -r -t 10 header <&3; do
    [[ -z ${header%$'\r'} ]] && break
done
cat <&3
echo
exec 3<&-

[[ $status =~ ^HTTP/[0-9.]+\ 2[0-9][0-9]( |$) ]] || exit 1
