# Render Docker runtime; set Root Directory = SmartLibrary.Api.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartLibrary.Api.csproj ./
RUN dotnet restore ./SmartLibrary.Api.csproj
COPY . ./
RUN dotnet publish ./SmartLibrary.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
# ASP.NET Core static-web-assets startup expects this directory even with Cloudinary.
RUN mkdir -p /app/wwwroot/covers
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "SmartLibrary.Api.dll"]
