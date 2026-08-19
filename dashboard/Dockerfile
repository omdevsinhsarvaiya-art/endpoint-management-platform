# ---------------------------------------------------------------------------
# Dashboard — static Vite build served by nginx, which also reverse-proxies the
# Admin API.
#
# Build context is the DASHBOARD DIRECTORY:
#
#   docker build -f dashboard/Dockerfile dashboard
#
# The proxy is not a convenience. The dashboard calls the API as the relative
# path /api/..., and the session cookie carries the __Host- prefix with Secure
# and SameSite=Strict — so the browser pins it to exactly one host and refuses
# to send it anywhere else. Serving the dashboard and the Admin API on separate
# hostnames breaks sign-in outright. Putting both behind this one nginx is what
# keeps them same-origin.
# ---------------------------------------------------------------------------

FROM node:24-alpine AS build
WORKDIR /src

# npm ci needs the lockfile and installs exactly what it pins.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# tsc -b runs first (see package.json), so a type error fails the image build
# rather than shipping a broken bundle.
RUN npm run build

FROM nginx:1.27-alpine AS runtime

COPY --from=build /src/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
