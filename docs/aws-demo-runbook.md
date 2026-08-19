# AWS demo runbook

Operating the demonstration environment: what is deployed, how to watch it, how
to release a change, and how to get back to a working state when a release goes
wrong.

This is a **demonstration** environment on a single EC2 host. It is not
production, and the shortcuts it takes are called out where they matter.

## What is running

| | |
|---|---|
| Region | `ap-south-1` (Mumbai) |
| Instance | `endpoint-platform-demo`, `t3.micro`, Ubuntu 24.04 LTS |
| Storage | 30 GB gp3, encrypted |
| Public URL | `https://65.2.37.254.nip.io` |
| TLS | Let's Encrypt, auto-renewing via certbot's systemd timer |
| Security group | 22 from the admin IP only; 80 and 443 public; nothing else |

Everything runs as Docker containers under
`infra/docker-compose.demo.yml` in `/home/ubuntu/app`:

| Service | Exposure |
|---|---|
| `dashboard` (nginx) | `127.0.0.1:8080` — host nginx proxies to it |
| `admin-api` | internal only |
| `agent-api` | internal only, reached at `/agent/` through nginx |
| `postgres` | internal only, **never published** |
| `redis` | internal only, **never published** |
| `migrations` | runs once per deploy, then exits |

Host nginx terminates TLS and proxies to the dashboard container. PostgreSQL and
Redis have no published ports at all — they are reachable only by the two APIs
on the compose-internal network.

### The name resolution caveat

`65.2.37.254.nip.io` is a wildcard-DNS name that resolves to the instance's IP.
The certificate is a genuine, publicly trusted Let's Encrypt certificate — it
was issued through a normal HTTP-01 challenge, and nothing about the TLS is
weakened.

However, **some ISPs intercept DNS and blackhole `nip.io`**, which is the case
on the network this was deployed from. Machines on such a network need one line
in their hosts file:

```
65.2.37.254   65.2.37.254.nip.io
```

- Windows: `C:\Windows\System32\drivers\etc\hosts` (edit as Administrator)
- Linux/macOS: `/etc/hosts`

Verify with `ping 65.2.37.254.nip.io` — it must answer from `65.2.37.254`, not
some other address. The permanent fixes are a real domain, or CloudFront once
the AWS account is verified for it.

## Connecting

```bash
ssh -i ~/.ssh/endpoint-platform-demo.pem ubuntu@65.2.37.254
cd /home/ubuntu/app
```

SSH is restricted to a single admin IP. If your public IP changes, update the
security group rule rather than opening `0.0.0.0/0`:

```bash
aws ec2 authorize-security-group-ingress --group-id sg-0c938edb24eada639 \
  --ip-permissions "IpProtocol=tcp,FromPort=22,ToPort=22,IpRanges=[{CidrIp=<new-ip>/32}]"
```

## Monitoring

A shell alias saves repeating the file and env flags:

```bash
alias dc='docker compose -f infra/docker-compose.demo.yml --env-file infra/.env'
```

| What | Command |
|---|---|
| Service status | `dc ps` |
| All logs, following | `dc logs -f` |
| One service | `dc logs -f admin-api` |
| Last 100 lines | `dc logs --tail=100 agent-api` |
| Container health | `docker inspect --format '{{.Name}} {{.State.Health.Status}}' $(docker ps -q)` |
| Memory and swap | `free -h` |
| Disk usage | `df -h /` |
| What Docker is using | `docker system df` |
| Volume list | `docker volume ls` |
| Application health | `curl -fsS https://65.2.37.254.nip.io/api/health/ready` |
| Agent API health | `curl -fsS http://127.0.0.1:8080/agent/../health/ready` (or via the container) |
| Certificate expiry | `sudo certbot certificates` |
| Renewal timer | `systemctl list-timers snap.certbot.renew.timer` |

From a workstation with AWS credentials:

```bash
aws ec2 describe-instances --instance-ids i-0859e6fa7161a49b3 \
  --query "Reservations[].Instances[].{State:State.Name,Ip:PublicIpAddress}" --output table
aws ec2 describe-instance-status --instance-ids i-0859e6fa7161a49b3 --output table
```

Deployment status: the **Actions** tab of the GitHub repository. Each run's
summary carries the deployed commit and the tail of the deploy log.

### Watch the disk

30 GB is not much once Docker has built several image generations. `docker
system df` shows the split; `docker image prune -f` reclaims superseded layers
and is run automatically at the end of every successful deploy.

## Releasing a change

Push to `main`. That is the whole workflow:

```
edit -> dotnet test -> npm run build -> commit -> push origin main
   -> GitHub Actions -> ssh to EC2 -> git checkout <sha>
   -> docker compose build -> up -d -> health checks -> done
```

The Actions workflow (`.github/workflows/deploy-demo.yml`) holds **no AWS
credentials**. It holds one SSH key whose `authorized_keys` entry pins
`command="/home/ubuntu/deploy.sh"` with forwarding and PTY disabled, so the key
cannot open a shell or read `infra/.env` — it can only request a redeploy. The
host then validates the requested commit: it must be a 40-hex SHA already
contained in `origin/main`, so no fork, branch, or deleted ref is deployable
even if the key leaks.

To deploy manually, on the host:

```bash
cd /home/ubuntu/app && git fetch origin main && ./deploy.sh
```

`deploy.sh` with no argument deploys `origin/main`.

### What a deploy does and does not touch

- Rebuilds images from the checked-out commit.
- Runs the migration job to completion before the APIs start. Migrations use the
  **owner** database role; the APIs keep the restricted role and no DDL rights.
- **Never touches the PostgreSQL volume.** Data survives deploys, rollbacks and
  container replacement.
- Prunes superseded image layers on success.

## Rollback

The deploy script rolls back on its own when a release fails to become healthy:
it re-checks out the previous commit, rebuilds, restarts, and re-runs the health
checks. If the rollback is also unhealthy it says so and stops rather than
looping.

To roll back deliberately to a known-good commit:

```bash
cd /home/ubuntu/app
git log --oneline -10                # pick the target
git checkout <known-good-sha>
docker compose -f infra/docker-compose.demo.yml --env-file infra/.env build
docker compose -f infra/docker-compose.demo.yml --env-file infra/.env up -d
docker compose -f infra/docker-compose.demo.yml --env-file infra/.env ps
```

Then confirm `https://65.2.37.254.nip.io` loads and sign-in works.

### Database rollback is not automatic, by design

EF Core migrations are applied forward only here. **Rolling code back does not
roll the schema back**, and it usually does not need to: the schema is additive,
so older code runs against a newer schema in almost every case.

A migration that drops or renames a column is the exception, and it must be
handled deliberately — take a dump first, and write a compensating migration
rather than reversing one in place:

```bash
docker exec epp-demo-postgres pg_dump -U "$POSTGRES_SUPERUSER" -d "$POSTGRES_DB" \
  > ~/backup-$(date +%F-%H%M).sql
```

Never `docker compose down -v` on this host: `-v` deletes the PostgreSQL volume,
and with it the audit trail, the enrolled devices and the administrator account.
`docker compose down` alone is safe.

## Secrets

`infra/.env` lives only on the instance, `chmod 600`, and is git-ignored. It was
generated on the host and its values have never been printed or committed. It
holds the PostgreSQL passwords, the Redis password, `SECRET_PROTECTION_KEY` and
`PUBLIC_ORIGIN`.

`SECRET_PROTECTION_KEY` must stay **identical for both APIs** — the Admin API
seals ephemeral account secrets and the Agent API redeems them. Deploys do not
regenerate it; rotating it would only invalidate secrets that are in flight at
that moment, which fail safely and can be re-issued.

To rotate a credential, edit `infra/.env` on the host and recreate the affected
services. Changing a PostgreSQL password also needs `ALTER ROLE` inside the
database — the password in the file is not what the server enforces once the
volume is initialised.

## Windows agent

The agent stays a native Windows service on the managed endpoint. It is **not**
containerised and must not be: it manages local accounts through `netapi32` and
needs real Windows elevation.

It dials AWS outbound over HTTPS, so no inbound rule, VPN, or public IP is
needed on the Windows machine:

```powershell
$env:ENDPOINTAGENT_Agent__ServerBaseUrl = 'https://65.2.37.254.nip.io'
```

The agent enforces certificate validation in Release builds. The
accept-any-certificate escape hatch is gated on both an explicit option and a
Debug build, and must not be enabled to make a demo easier.

## Cost

| Resource | Monthly |
|---|---|
| EC2 t3.micro | $0 while free-tier/credit eligible, else ~$7.50 |
| 30 GB gp3 | $0 within the allowance, else ~$2.40 |
| Public IPv4 | ~$3.60 — charged regardless of free tier |
| **Expected** | **~$3.60** |

To stop charges entirely, terminate the instance — but that destroys the
PostgreSQL volume with it, so take a dump first if the data matters. Stopping
(rather than terminating) still bills the EBS volume and releases the public IP,
which changes the URL and invalidates the certificate.
