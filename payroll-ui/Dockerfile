# Step 1: Build the React application
FROM node:22-alpine AS build
WORKDIR /app/payroll-ui

# Build from the repository root: docker build -f payroll-ui/Dockerfile .
COPY payroll-ui/package*.json ./
RUN npm ci
COPY payroll-ui/ ./
COPY shared/ /app/shared/
RUN npm run build

# Step 2: Serve the static files with Nginx
FROM nginx:alpine
COPY --from=build /app/payroll-ui/dist /usr/share/nginx/html
COPY payroll-ui/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
