# Small independent reference-wallet oracle. Build from repository root after vendored vectors are present.
FROM python:3.13-slim
WORKDIR /oracle
COPY test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Vectors/ /oracle/reference/
COPY test/Docker/silentpayments_reference/oracle.py /oracle/oracle.py
# Test-owned pod stays up for kubernetes exec calls; no external service receives wallet keys.
CMD ["python3", "-c", "import time; time.sleep(86400)"]
