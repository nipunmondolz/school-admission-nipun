#!/bin/sh
# Runs automatically when the nginx container starts (docker-entrypoint.d).
# Writes the backend URL, taken from the API_URL environment variable, into config.js.
set -e
API_URL="${API_URL:-http://localhost:8080}"
printf 'window.__APP_CONFIG__ = { API_URL: "%s" };\n' "$API_URL" > /usr/share/nginx/html/config.js
