FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Render injects PORT at runtime; Program.cs reads it directly.
ENV ASPNETCORE_HTTP_PORTS=""
EXPOSE 8080

ENTRYPOINT ["dotnet", "QueueStormInvestigator.dll"]
