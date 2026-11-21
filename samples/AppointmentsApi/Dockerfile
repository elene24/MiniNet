# Build from the repository root:
#   docker build -f samples/AppointmentsApi/Dockerfile -t mininet-appointments .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
COPY samples/AppointmentsApi/ samples/AppointmentsApi/
COPY infra/sql/ infra/sql/
RUN dotnet publish samples/AppointmentsApi/AppointmentsApi.csproj -c Release -o /app

# The plain .NET runtime image is enough: MiniNet does not use ASP.NET Core.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AppointmentsApi.dll"]
