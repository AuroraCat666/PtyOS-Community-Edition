FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY RPGR-Server.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 8080 26377
ENV SMTP_PASS=""
ENTRYPOINT ["sh", "-c", "dotnet RPGR-Server.dll --host \"*\" --http 8080 --tcp 26377 --data /data --public-url https://ptyos.yuncishu.xyz --public-tcp-host ptyos.yuncishu.xyz --smtp-host smtp.163.com --smtp-port 465 --smtp-user FireLight2026@163.com --smtp-pass \"$SMTP_PASS\" --smtp-from FireLight2026@163.com"]