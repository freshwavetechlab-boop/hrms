# Step 1: Build the React application
FROM node:22-alpine AS build
WORKDIR /app

# Change these to look locally in the current directory
COPY package*.json ./
RUN npm ci
COPY . ./
RUN npm run build

# Step 2: Serve the static files with Nginx
FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
