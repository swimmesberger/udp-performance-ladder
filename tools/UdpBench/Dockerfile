# Build from the repository root:
#   docker build -f tools/UdpBench/Dockerfile -t udpbench .
# Run with host networking; a bridge network adds its own forwarding
# layer and pollutes the measurement:
#   docker run --rm --network host udpbench send --target <host:port> --rate 250000
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY tools/UdpBench tools/UdpBench
RUN dotnet publish tools/UdpBench/UdpBench.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "UdpBench.dll"]
