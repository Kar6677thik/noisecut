#!/usr/bin/env bash
# ONE-TIME bootstrap, step 2: print a kubeconfig for the noisecut-deployer ServiceAccount.
#
#   ./deploy/k3s/bootstrap/make-kubeconfig.sh <api-server-url> [tls-server-name] > noisecut-deployer.kubeconfig
#
#   <api-server-url>   how CI reaches the API server, e.g. https://<server-tailnet-ip>:6443
#   [tls-server-name]  a name that IS in the API server certificate, when the URL's host is not (e.g. "kubernetes").
#                      k3s only puts the names/IPs it knows about at install time into its certificate; a Tailscale IP
#                      usually isn't one of them. This tells the client which name to check the certificate against.
#
# Then store it as a GitHub secret (base64, one line):  base64 -w0 noisecut-deployer.kubeconfig
# and delete the local file. Run with a kubeconfig that can read secrets in the noisecut namespace.
set -euo pipefail

server="${1:?usage: make-kubeconfig.sh <api-server-url> [tls-server-name]}"
tls_name="${2:-}"
ns=noisecut
secret=noisecut-deployer-token

token=$(kubectl -n "$ns" get secret "$secret" -o jsonpath='{.data.token}' | base64 -d)
ca=$(kubectl -n "$ns" get secret "$secret" -o jsonpath='{.data.ca\.crt}')

cat <<EOF
apiVersion: v1
kind: Config
clusters:
  - name: home
    cluster:
      server: ${server}
      certificate-authority-data: ${ca}
$( [ -n "$tls_name" ] && echo "      tls-server-name: ${tls_name}" )
users:
  - name: noisecut-deployer
    user:
      token: ${token}
contexts:
  - name: noisecut
    context:
      cluster: home
      user: noisecut-deployer
      namespace: ${ns}
current-context: noisecut
EOF
