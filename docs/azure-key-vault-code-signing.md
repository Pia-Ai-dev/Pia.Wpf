# Azure Key Vault Code Signing in GitHub Actions

This document describes how to move Pia.Wpf release signing from a manual local
process (Azure SignTool with cert-based auth on a developer workstation) to the
`build-and-release.yml` GitHub Actions workflow.

It is split into three parts:

1. **Decision** — pick one of two authentication options.
2. **Admin checklist** — step-by-step setup for the Azure and GitHub admin.
3. **Implementation plan** — the workflow changes that follow once the admin
   work is done.

---

## 1. Decision: which authentication option applies?

Both options use the same code signing certificate stored in Azure Key Vault.
They differ only in **how the GitHub Actions runner proves its identity to Azure**.

| | **Option A — OIDC (federated credentials)** | **Option B — Service principal with client secret** |
|---|---|---|
| Long-lived secret stored in GitHub | None | Yes (client secret) |
| Secret rotation | Not required | Required (Azure default lifetime ≤ 24 months) |
| Azure setup complexity | Slightly higher (federated credential subject must match GitHub ref) | Lower |
| Closest to current manual flow | No | Yes |
| Recommended by Microsoft / GitHub | Yes | Legacy |

**Pick Option A** unless your tenant policy forbids federated credentials, or
your admin needs to ship something today and OIDC setup is a blocker. You can
start with Option B and migrate later — the workflow change between the two is
small.

The rest of this document describes both. Steps that apply to **only one
option** are tagged `[A]` or `[B]`.

---

## 2. Admin checklist

### 2.1 Prerequisites (both options)

The following must already exist. If not, the Azure admin creates them once.

- [ ] **Azure Key Vault** with the code signing certificate imported.
  - Vault URL, e.g. `https://piasigning.vault.azure.net/`
  - Certificate name in the vault, e.g. `PiaCodeSigning`
- [ ] **App Registration** in Microsoft Entra ID (Azure AD) that will represent
  the GitHub Actions runner. Note its:
  - Application (client) ID
  - Directory (tenant) ID
  - Subscription ID of the vault
- [ ] **Key Vault access policy or RBAC role** granting the App Registration:
  - Certificate permission: **Get**
  - Key permission: **Sign**
  *(Under RBAC: assign role `Key Vault Crypto User` on the vault scope, plus
  `Key Vault Certificate User` if certificates are managed via RBAC.)*

### 2.2 Option A — OIDC federated credential `[A]`

Add a federated credential to the App Registration so the GitHub Actions runner
can exchange its workflow token for an Azure access token without storing a
secret.

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → select the
   Pia signing app → **Certificates & secrets** → **Federated credentials** →
   **Add credential**.
2. Scenario: **GitHub Actions deploying Azure resources**.
3. Fill in:
   - Organization: `Pia-Ai-dev`
   - Repository: `Pia.Wpf`
   - Entity type: **Branch**
   - Branch name: `main`
   - Name: `pia-wpf-main`
4. Save. Repeat for any additional release branches you sign from
   (e.g. `release/*` — entity type **Branch** with the literal pattern).
5. *(Optional)* Add a second credential for tag-based releases:
   - Entity type: **Tag**, value: `v*` — required if you ever trigger the
     workflow from a pushed tag rather than a branch.

> **Note on subjects.** GitHub's OIDC subject is
> `repo:Pia-Ai-dev/Pia.Wpf:ref:refs/heads/<branch>`. The portal builds this for
> you; if you script it, build it manually.

### 2.3 Option B — client secret `[B]`

1. Azure Portal → App Registration → **Certificates & secrets** → **Client
   secrets** → **New client secret**.
2. Description: `github-actions-pia-wpf`. Expiry: shortest acceptable (≤ 24
   months). Copy the **value** immediately — it is shown only once.
3. Record the expiry date in the team's secret-rotation calendar.

### 2.4 GitHub repository secrets

In `Pia-Ai-dev/Pia.Wpf` → **Settings** → **Secrets and variables** → **Actions**,
add:

| Secret name | Value | Option |
|---|---|---|
| `AZURE_CLIENT_ID` | App Registration's Application (client) ID | A + B |
| `AZURE_TENANT_ID` | Directory (tenant) ID | A + B |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID containing the Key Vault | A only |
| `AZURE_KEY_VAULT_URL` | e.g. `https://piasigning.vault.azure.net/` | A + B |
| `AZURE_CERT_NAME` | Certificate name in the vault | A + B |
| `AZURE_CLIENT_SECRET` | Client secret value from 2.3 | B only |

### 2.5 GitHub workflow permissions `[A]`

Option A requires the workflow to request an OIDC token. The repository must
allow this:

- **Settings** → **Actions** → **General** → **Workflow permissions** →
  ensure *"Allow GitHub Actions to create and approve pull requests"* is not
  required (it isn't), but the YAML must declare `id-token: write`. The
  implementation plan below adds this.

### 2.6 Verification (admin)

Before the workflow change is merged, the admin should confirm:

- [ ] AzureSignTool, run from the admin's machine using the **same App
  Registration** (e.g. `az login --service-principal …` for Option B, or
  manually obtained token for Option A), can sign a test exe against the vault.
- [ ] The test signature validates with `signtool verify /pa /v test.exe`.
- [ ] *(Option A)* The federated credential subject pattern matches the
  branches/tags from which releases will run.

If verification passes, hand over to the developer to apply the implementation
plan.

---

## 3. Implementation plan (workflow changes)

These changes live in `.github/workflows/build-and-release.yml`. They assume
section 2 is complete.

### 3.1 Add `id-token: write` permission `[A only]`

```yaml
permissions:
  contents: write
  id-token: write   # required for OIDC federated credential
```

For Option B leave the existing `permissions:` block unchanged.

### 3.2 Install AzureSignTool and (Option A) authenticate to Azure

Insert these steps after `Setup .NET 10`.

**Option A:**

```yaml
      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Install AzureSignTool
        run: dotnet tool install --global AzureSignTool
```

**Option B:** no `azure/login` step.

```yaml
      - name: Install AzureSignTool
        run: dotnet tool install --global AzureSignTool
```

### 3.3 Sign the published exe before packing

Velopack copies the publish output verbatim into the package, so an unsigned
`Pia.Wpf.exe` going into `vpk pack` produces an unsigned exe inside the MSI
even if the MSI itself is later signed. Sign the exe immediately after
`Publish self-contained`.

**Option A:**

```yaml
      - name: Sign published Pia.Wpf.exe
        shell: pwsh
        run: |
          AzureSignTool sign `
            -kvu "${{ secrets.AZURE_KEY_VAULT_URL }}" `
            -kvc "${{ secrets.AZURE_CERT_NAME }}" `
            -kvm `
            -tr http://timestamp.digicert.com `
            -td sha256 `
            -fd sha256 `
            -v `
            publish/Pia.Wpf.exe
```

**Option B:** replace `-kvm` with explicit credentials.

```yaml
      - name: Sign published Pia.Wpf.exe
        shell: pwsh
        run: |
          AzureSignTool sign `
            -kvu "${{ secrets.AZURE_KEY_VAULT_URL }}" `
            -kvi "${{ secrets.AZURE_CLIENT_ID }}" `
            -kvt "${{ secrets.AZURE_TENANT_ID }}" `
            -kvs "${{ secrets.AZURE_CLIENT_SECRET }}" `
            -kvc "${{ secrets.AZURE_CERT_NAME }}" `
            -tr http://timestamp.digicert.com `
            -td sha256 `
            -fd sha256 `
            -v `
            publish/Pia.Wpf.exe
```

### 3.4 Pass `--signTemplate` to both `vpk pack` invocations

`vpk` uses the template to sign the Velopack-injected binaries (`Update.exe`,
`Setup.exe`) and the MSI. Add the same flag to both the per-machine and the
per-user pack steps.

**Option A:**

```yaml
          --signTemplate "AzureSignTool sign -kvu ${{ secrets.AZURE_KEY_VAULT_URL }} -kvc ${{ secrets.AZURE_CERT_NAME }} -kvm -tr http://timestamp.digicert.com -td sha256 -fd sha256 {{file}}"
```

**Option B:**

```yaml
          --signTemplate "AzureSignTool sign -kvu ${{ secrets.AZURE_KEY_VAULT_URL }} -kvi ${{ secrets.AZURE_CLIENT_ID }} -kvt ${{ secrets.AZURE_TENANT_ID }} -kvs ${{ secrets.AZURE_CLIENT_SECRET }} -kvc ${{ secrets.AZURE_CERT_NAME }} -tr http://timestamp.digicert.com -td sha256 -fd sha256 {{file}}"
```

> The template is one line — the `>`-folded block already in use for `vpk pack`
> arguments handles that. `{{file}}` is the Velopack placeholder, not GitHub
> Actions interpolation; leave it literal.

### 3.5 Verification (developer)

After the change is merged:

- [ ] First run of the workflow succeeds end-to-end.
- [ ] Download the produced `Pia.Wpf-win.msi` and run
  `signtool verify /pa /v Pia.Wpf-win.msi` — should report a valid signature
  chain anchored on the code signing certificate.
- [ ] Open the MSI and verify `Pia.Wpf.exe` inside is signed (Properties →
  Digital Signatures).
- [ ] Verify `Update.exe` and `Setup.exe` (extract the `*.nupkg` if needed) are
  signed.
- [ ] Windows SmartScreen no longer warns on first launch (this can take a few
  releases to reach reputation; verify the *signature*, not the absence of the
  warning).

### 3.6 Rollback

If signing fails or breaks the release, revert the workflow file to the prior
commit. No Azure-side cleanup is required to roll back; the App Registration
and Key Vault permissions can stay in place.

---

## 4. Notes

- **Timestamp server.** `http://timestamp.digicert.com` is used throughout.
  Alternatives: `http://timestamp.sectigo.com`, `http://timestamp.globalsign.com/tsa/r6advanced1`.
  Match what your CA recommends.
- **Algorithms.** `-td sha256 -fd sha256` is correct for SHA-256 file digest
  and SHA-256 timestamp digest. SHA-1 is no longer accepted by Windows for new
  signatures.
- **AzureSignTool version.** Requires .NET 8+; `actions/setup-dotnet@v4` with
  `10.0.x` satisfies this.
- **Cost.** Each `AzureSignTool sign` call performs one Key Vault sign
  operation, billed per operation. A typical Pia release signs ≈ 4 files
  (`Pia.Wpf.exe`, `Update.exe`, `Setup.exe`, MSI) per pack, ≈ 8 per workflow
  run because the per-user MSI repacks. Negligible at release cadence but
  worth knowing if a future change loops over many files.
