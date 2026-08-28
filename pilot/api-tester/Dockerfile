# Pilot: wrap the EXISTING api-tester image with @beehive/telemetry-preload —
# instrumentation with zero changes to the app's source repo. The app's own
# CMD/entrypoint is untouched; NODE_OPTIONS (set in fly.toml [env]) makes Node
# load the preload before the app.
FROM node:24-alpine AS preload-deps
COPY telemetry-preload /opt/telemetry-preload
RUN cd /opt/telemetry-preload && npm install --omit=dev

FROM registry.fly.io/api-tester:sha-242ad7acaeb49b74b3dca7819ae21a9dcba9a145@sha256:f5f941e03a19778af091727018622e08651862b461b86e6932099a1a56d8c64c
COPY --from=preload-deps /opt/telemetry-preload /opt/telemetry-preload
# Preload ONLY the server process. The image's default `npm run start` boots
# nest's TS compiler — on a 512MB machine, instrumenting npm+nest+tsc via
# NODE_OPTIONS starves it before it ever listens. The image ships a built
# dist/, so run it directly, the same shape as bo-api's production CMD.
CMD ["node", "--require", "/opt/telemetry-preload", "dist/main"]
