#!/bin/bash
set -e

# Start Consul agent in client mode
consul agent -config-dir=/etc/consul.d -data-dir=/consul/data &

# Wait for Consul to be ready
for i in $(seq 1 30); do
    if consul info >/dev/null 2>&1; then
        echo "Consul agent is ready."
        break
    fi
    echo "Waiting for Consul agent... ($i/30)"
    sleep 1
done

# Start the .NET application
exec dotnet CodeGraph.dll
