#!/usr/bin/env bash
# Gets an access token using OAuth2 auth code flow with a local redirect.
# Usage: ./scripts/get-token.sh <client-id> <tenant-id> [scope]
set -euo pipefail

CLIENT_ID="${1:?Usage: $0 <client-id> <tenant-id> [scope]}"
TENANT_ID="${2:?Usage: $0 <client-id> <tenant-id> [scope]}"
SCOPE="${3:-api://$CLIENT_ID/access_mcp}"
REDIRECT_URI="http://localhost:3456"
AUTH_URL="https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0"

STATE=$(openssl rand -hex 16)

# PKCE: generate code_verifier and code_challenge
CODE_VERIFIER=$(openssl rand -base64 32 | tr -d '=+/' | head -c 43)
CODE_CHALLENGE=$(printf '%s' "$CODE_VERIFIER" | openssl dgst -sha256 -binary | base64 | tr '+/' '-_' | tr -d '=')

echo "Opening browser for login..."
open "$(printf '%s/authorize?client_id=%s&response_type=code&redirect_uri=%s&scope=%s%%20offline_access&state=%s&code_challenge=%s&code_challenge_method=S256' \
  "$AUTH_URL" "$CLIENT_ID" "$REDIRECT_URI" "$SCOPE" "$STATE" "$CODE_CHALLENGE")"

echo "Waiting for callback on $REDIRECT_URI ..."

# Use Python to run a one-shot HTTP server and capture the auth code
CODE=$(python3 -c "
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        qs = parse_qs(urlparse(self.path).query)
        code = qs.get('code', [''])[0]
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'<html><body><h2>Done! You can close this tab.</h2></body></html>')
        print(code, flush=True)

    def log_message(self, *a): pass

s = HTTPServer(('127.0.0.1', 3456), Handler)
s.handle_request()
")

if [[ -z "$CODE" ]]; then
  echo "ERROR: Failed to capture auth code from callback." >&2
  exit 1
fi

TOKEN_RESPONSE=$(curl -s -X POST "$AUTH_URL/token" \
  -d "client_id=$CLIENT_ID" \
  -d "grant_type=authorization_code" \
  -d "code=$CODE" \
  -d "redirect_uri=$REDIRECT_URI" \
  -d "scope=$SCOPE" \
  -d "code_verifier=$CODE_VERIFIER")

ACCESS_TOKEN=$(echo "$TOKEN_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('access_token',''))" 2>/dev/null)

if [[ -z "$ACCESS_TOKEN" ]]; then
  echo "ERROR: Token exchange failed:" >&2
  echo "$TOKEN_RESPONSE" | python3 -m json.tool 2>/dev/null || echo "$TOKEN_RESPONSE"
  exit 1
fi

echo ""
echo "$ACCESS_TOKEN"
