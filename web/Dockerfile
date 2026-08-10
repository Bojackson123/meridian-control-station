# The console: the Vite bundle, served by nginx, which also reverse-proxies /api to the API
# container. Build context is this directory:
#
#     docker build -t mcs-web ./web
#
# Node is pinned to the version in .nvmrc so the image and a developer's machine build the
# console with the same toolchain, and a "works on my laptop" bundle is not a thing that can
# happen quietly.
FROM node:24.18.1-alpine AS build
WORKDIR /app

#  npm ci, not npm install: ci installs the lockfile exactly and fails if package.json has
#  drifted from it, which is precisely the property a build wants and precisely the property
#  install is willing to give up. Lockfile ahead of the source, for the same caching reason
#  as the API image.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
#  `npm run build` runs `tsc -b` before vite build, so a type error fails the image here
#  rather than shipping a bundle that compiled around it.
RUN npm run build


# nginx:alpine runs as root in order to bind :80. The unprivileged variant costs one port
# number -- it listens on 8080 -- and is the better answer for a container whose whole job is
# serving static files and forwarding requests.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime

#  The whole of dist/, which includes the offline basemap style copied out of web/public.
#  Nothing this page loads may come from another origin, so anything missing here is not a
#  degraded map, it is a blank one.
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx/default.conf /etc/nginx/conf.d/default.conf

EXPOSE 8080
