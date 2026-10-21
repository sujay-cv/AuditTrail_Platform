# 1) Build the static site
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

# 2) Serve with nginx. API_BASE_URL is injected at container start (config.js + CSP), not baked into the build.
FROM nginx:1.27-alpine
COPY nginx/default.conf.template nginx/security-headers.inc.template /etc/nginx/templates/
COPY nginx/40-write-config.sh /docker-entrypoint.d/40-write-config.sh
RUN chmod +x /docker-entrypoint.d/40-write-config.sh
COPY --from=build /app/dist /usr/share/nginx/html
ENV API_BASE_URL=http://localhost:8080
EXPOSE 80
