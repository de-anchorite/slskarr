FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Slskarr.slnx ./
COPY src/Slskarr/Slskarr.csproj src/Slskarr/
RUN dotnet restore src/Slskarr/Slskarr.csproj
COPY src/ src/
RUN dotnet publish src/Slskarr/Slskarr.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Slskarr__DataPath=/data
RUN mkdir -p /data && chown app:app /data
COPY --from=build /out .
USER app
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Slskarr.dll"]
