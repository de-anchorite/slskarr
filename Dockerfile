FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Slskarr.slnx ./
COPY src/Slskarr/Slskarr.csproj src/Slskarr/
RUN dotnet restore src/Slskarr/Slskarr.csproj
COPY src/ src/
# Release builds pass the tag without the leading v, e.g. --build-arg VERSION=1.2.3
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Slskarr/Slskarr.csproj -c Release --no-restore -o /out -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG VERSION=0.0.0-dev
LABEL org.opencontainers.image.version=$VERSION
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Slskarr__DataPath=/data
RUN mkdir -p /data && chown app:app /data
COPY --from=build /out .
USER app
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Slskarr.dll"]
