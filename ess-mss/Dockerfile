# Step 1: Build the React application
FROM node:22-alpine AS build
WORKDIR /app/ess-mss

# Build from the repository root: docker build -f ess-mss/Dockerfile .
COPY ess-mss/package*.json ./
RUN npm ci
COPY ess-mss/ ./
COPY shared/ /app/shared/
RUN npm run build

# Step 2: Serve the static files with Nginx
FROM nginx:alpine
COPY --from=build /app/ess-mss/dist /usr/share/nginx/html
COPY ess-mss/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
