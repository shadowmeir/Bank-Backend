# =========================
# Build stage
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only project files first (better Docker layer caching)
COPY src/Bank.Api/Bank.Api.csproj src/Bank.Api/
COPY src/Bank.Application/Bank.Application.csproj src/Bank.Application/
COPY src/Bank.Domain/Bank.Domain.csproj src/Bank.Domain/
COPY src/Bank.Infrastructure/Bank.Infrastructure.csproj src/Bank.Infrastructure/

# Restore only the API project (brings transitive dependencies)
RUN dotnet restore src/Bank.Api/Bank.Api.csproj

# Copy the rest of the repository
COPY . .

# Publish the API
RUN dotnet publish src/Bank.Api/Bank.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# =========================
# Runtime stage
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Kestrel listens on 8080 inside the container
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# Copy published output
COPY --from=build /app/publish .

# Run the API
ENTRYPOINT ["dotnet", "Bank.Api.dll"]