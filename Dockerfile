# 建置：sdk 階段 publish Web 與 Eval；執行：web（網頁）與 seed（重建資料庫的一次性工作）。
# 使用 Debian 基底（不用 Alpine／invariant 模式）：SqlClient 與 Bogus 的 zh_TW 資料需要 ICU。
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/GeoNl2Sql.Web/GeoNl2Sql.Web.csproj -c Release -o /out/web \
 && dotnet publish eval/GeoNl2Sql.Eval/GeoNl2Sql.Eval.csproj -c Release -o /out/eval

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS web
WORKDIR /app
COPY --from=build /out/web .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GeoNl2Sql.Web.dll"]

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS seed
WORKDIR /app
COPY --from=build /out/eval .
USER $APP_UID
ENTRYPOINT ["dotnet", "GeoNl2Sql.Eval.dll", "seed"]
