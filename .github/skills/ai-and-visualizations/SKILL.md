---
name: ai-and-visualizations
description: 'Configure and work with Query Gate AI query assistant (BYOAI), interactive entity-relationship (ER) diagrams, visual DAG execution plans, and enterprise OIDC single sign-on.'
---

# AI Assistant, Visualizations & Enterprise SSO

Query Gate provides intelligent query assistance, interactive visual schema exploration, graphical execution plan inspection, and federated enterprise authentication.

## Core Files & Services

### 1. AI Query Assistant (BYOAI)
- Interfaces & Clients: `Services/Ai/IAiClient.cs`, `OpenAiCompatibleAiClient.cs`, `GeminiAiClient.cs`, `AnthropicAiClient.cs`
- Orchestrator: `Services/Ai/AiAssistantService.cs`
- Admin Configuration: `Components/Pages/Admin/Settings.razor` (under AI Assistant tab)
- Query Workspace UI: `Components/Pages/Query.razor` (Prompt bar `Ctrl+I`, error banner fix button, plan explainer)
- Test Suite: `tests/DbAccessManager.Tests/ErDiagramAndAiTests.cs`

### 2. Interactive Entity-Relationship (ER) Diagrams
- Data Transfer Objects: `Services/ErDiagramDto.cs` (`ErDiagramGraphDto`, `ErTableNodeDto`, `ErColumnDto`, `ErRelationshipEdgeDto`)
- Metadata Extraction: `Services/InfraServices.cs` (`MetadataService.GetErDiagramDataAsync`)
- SVG Canvas Engine: `wwwroot/js/er-diagram.js` (draggable cards, dynamic Bezier paths, pan and zoom)
- Modal Component: `Components/Shared/ErDiagramModal.razor`
- Script Registration: `Components/App.razor` (`<script src="js/er-diagram.js"></script>`)

### 3. Visual Execution Plan Tree (Interactive DAG)
- Plan Hierarchy & Parsers: `Services/ExecutionPlanParser.cs` (`PlanOperatorDto.ParentNodeId`, `ChildrenNodeIds`)
- Engine Implementations: `Services/Providers/PostgreSqlDatabaseProvider.cs`, `MySqlDatabaseProvider.cs`, `SqlServerDatabaseProvider.cs`
- UI Tree Renderer: `Components/Pages/Query.razor` (3-way view toggle, `RenderPlanTreeNode`, bottleneck badges)
- Unit Tests: `tests/DbAccessManager.Tests/ExecutionPlanParserTests.cs`

### 4. Enterprise OIDC Single Sign-On (Strict Authorized-Only)
- Service: `Services/OidcService.cs` (`OidcSettings`, test discovery endpoint)
- Challenge & Flow: `Components/Account/Pages/ExternalLogin.razor`
- Callback & Authorization: `Components/Account/Pages/ExternalLoginCallback.razor`
- Sign-In Button: `Components/Account/Pages/Login.razor`
- Admin Configuration: `Components/Pages/Admin/Settings.razor` (under SSO & OIDC tab)

---

## Architectural Principles & Security Rules

### Privacy-First BYOAI Architecture
1. **Zero Table Data Leakage**:
   `AiAssistantService.BuildCompactSchemaContextAsync` queries table names, column names, data types, nullability, and foreign key definitions from `MetadataService`. It never executes queries against table data or transmits customer rows to external AI providers.
2. **Server-Side AST Pre-Validation**:
   Before generated SQL is returned to the UI, it passes through `SqlGate.Analyze`.
   - Checks syntax errors and parser failures.
   - Detects statement permissions (SELECT, INSERT, UPDATE, DELETE, etc.).
   - Emits immediate warnings if destructive DDL (DROP, TRUNCATE, ALTER) is present or if UPDATE/DELETE statements lack WHERE clauses.
3. **Bring Your Own AI (BYOAI)**:
   Query Gate ships with no default or hardcoded AI provider credentials. Features remain inactive until an administrator enters an API key or custom endpoint in Admin Settings.
4. **Encrypted Secret Storage**:
   All API keys and client secrets are encrypted using `AesGcmEncryptionService` before persisting in the `Settings` table.

### ER Diagram Performance & Traversal
1. **Neighborhood Graph Traversal (1-Hop & 2-Hop)**:
   Full schema extraction can be slow or unreadable on databases with hundreds of tables. By default, `GetErDiagramDataAsync` takes a `focusTable` and performs a breadth-first search (BFS) up to `maxHops` (default 1, selectable up to 2).
2. **On-Demand Full Schema**:
   Setting `maxHops = 0` loads all accessible tables and foreign keys in the active database.
3. **Client-Side SVG Acceleration**:
   `er-diagram.js` manages drag interactions, pan/zoom transformations, and Bezier connector updates entirely in client memory without server round-trips.

### Execution Plan DAG Structure
1. **Hierarchical Operator Tree**:
   `PlanOperatorDto` contains `ParentNodeId` and `ChildrenNodeIds`.
2. **Multi-Engine Parsing**:
   - SQL Server: Parsed from `RelOp` XML hierarchy with `EstimatedTotalSubtreeCost`.
   - PostgreSQL: Parsed from nested JSON `Plans` arrays with `Total Cost`.
   - MySQL & MariaDB: Parsed from JSON query blocks and nested loop structures.
3. **Bottleneck Indicators**:
   Operators consuming >= 30% of total plan cost are visually highlighted with warning badges.

### Strict Authorized-Only OIDC Authentication
1. **Pre-Authorized Accounts Only**:
   In `ExternalLoginCallback.razor`, when an identity provider returns claims for an email, the user is resolved via `UserManager.FindByEmailAsync`.
   - If the user does not exist or `IsActive == false`, authentication is denied immediately.
   - Self-registration from untrusted identity providers is blocked by design to prevent unauthorized database access.
2. **Fail-Closed Auditing**:
   Every OIDC login event (successful login, unknown user denied, inactive account denied) is logged through `AuditService.TryWriteAsync`.

---

## Testing & Verification Commands

Run unit tests for AI and visualization services:
```bash
DOTNET_CLI_HOME=/tmp/dotnet dotnet test tests/DbAccessManager.Tests/DbAccessManager.Tests.csproj --no-restore --filter "FullyQualifiedName~ErDiagramAndAiTests|FullyQualifiedName~ExecutionPlanParserTests"
```
