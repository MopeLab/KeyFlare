FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY KeyFlare.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release --no-restore --self-contained false -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0
LABEL org.opencontainers.image.source="https://github.com/MopeLab/KeyFlare"
WORKDIR /app
COPY --from=build /publish/ ./
RUN mkdir -p App_Data && chown "$APP_UID" App_Data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "KeyFlare.dll"]
