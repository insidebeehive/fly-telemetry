# Smoke-test app image — mirrors the documented integration exactly:
# plain node CMD, telemetry activated via NODE_OPTIONS in fly.toml.
FROM node:24-slim
ENV NODE_ENV=production
WORKDIR /app
COPY package.json ./
RUN npm install --no-audit --no-fund
COPY server.js server.mjs ./
# ESM entry deliberately — proves the --import loader-hook path in production.
CMD ["node", "server.mjs"]
