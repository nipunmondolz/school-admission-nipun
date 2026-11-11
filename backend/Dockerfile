FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Api/Api.csproj src/Api/
COPY tests/Api.Tests/Api.Tests.csproj tests/Api.Tests/
RUN dotnet restore tests/Api.Tests/Api.Tests.csproj
COPY . .
RUN dotnet publish src/Api/Api.csproj -c Release -o /out --no-restore

# `docker compose run --rm tests` builds and runs this stage.
FROM build AS test
CMD ["dotnet", "test", "tests/Api.Tests/Api.Tests.csproj", "--no-restore"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SchoolAdmission.Api.dll"]
