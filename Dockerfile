# Stage 1: Base image with ASP.NET Core runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Create the Resume directory and ensure it has write permissions
RUN mkdir -p /app/Resume && chown -R app:app /app/Resume

# Stage 2: SDK image for compiling the application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["JobAutomation.csproj", "."]
RUN dotnet restore "./JobAutomation.csproj"

# Copy the remaining source code
COPY . .

# Build the project
RUN dotnet build "JobAutomation.csproj" -c Release -o /app/build

# Stage 3: Publish the compiled application
FROM build AS publish
RUN dotnet publish "JobAutomation.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Persist the ownership for the Resume directory
RUN chown -R app:app /app/Resume

# Run as non-root user for security
USER app

ENTRYPOINT ["dotnet", "JobAutomation.dll"]
