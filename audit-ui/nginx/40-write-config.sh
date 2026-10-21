#!/bin/sh
# Runs at container start (nginx image executes /docker-entrypoint.d/*.sh): writes the runtime config the app reads.
set -eu
cat > /usr/share/nginx/html/config.js <<EOF
window.__APP_CONFIG__ = { apiBaseUrl: '${API_BASE_URL}' };
EOF
echo "config.js written with apiBaseUrl=${API_BASE_URL}"
