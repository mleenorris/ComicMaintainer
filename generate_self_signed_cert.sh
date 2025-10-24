#!/bin/bash
# Generate self-signed SSL certificate for development/testing

CERT_DIR=${1:-/Config/ssl}
DAYS=${2:-365}
DOMAIN=${3:-localhost}

echo "Generating self-signed SSL certificate..."
echo "Certificate directory: $CERT_DIR"
echo "Valid for: $DAYS days"
echo "Domain: $DOMAIN"

# Create directory if it doesn't exist
mkdir -p "$CERT_DIR"

# Generate private key and certificate
openssl req -x509 -nodes -days "$DAYS" -newkey rsa:2048 \
    -keyout "$CERT_DIR/selfsigned.key" \
    -out "$CERT_DIR/selfsigned.crt" \
    -subj "/C=US/ST=State/L=City/O=Organization/CN=$DOMAIN" \
    -addext "subjectAltName=DNS:$DOMAIN,DNS:*.${DOMAIN},IP:127.0.0.1"

if [ $? -eq 0 ]; then
    echo "✓ Certificate generated successfully!"
    echo "  Certificate: $CERT_DIR/selfsigned.crt"
    echo "  Private Key: $CERT_DIR/selfsigned.key"
    echo ""
    echo "To use these certificates, set the following environment variables:"
    echo "  SSL_CERTFILE=$CERT_DIR/selfsigned.crt"
    echo "  SSL_KEYFILE=$CERT_DIR/selfsigned.key"
    echo ""
    echo "⚠️  WARNING: Self-signed certificates are for development/testing only!"
    echo "    Browsers will show security warnings. For production, use certificates"
    echo "    from a trusted Certificate Authority (e.g., Let's Encrypt)."
else
    echo "✗ Failed to generate certificate"
    exit 1
fi
