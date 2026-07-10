# LaraFashion Codex Instructions

## Project Overview

LaraFashion is an ASP.NET Core Blazor Web App using interactive server rendering. The app targets `net9.0`, uses Entity Framework Core with SQLite, and stores product/order/admin/catalog data through `AppDbContext`.

The main project is `LaraFashion/LaraFashion.csproj`. Runtime setup is in `LaraFashion/Program.cs`. Core folders include:

- `LaraFashion/Components`: Blazor pages, layout, cart, store, modals, and admin UI.
- `LaraFashion/Models`: products, sizes, categories, discounts, cart, customers, orders, admin users, and enums.
- `LaraFashion/Services`: product, cart, order, discount, category, image maintenance, auth, browser storage, JWT, password hashing, and database seeding.
- `LaraFashion/Data`: `AppDbContext` and seed data.
- `LaraFashion/Migrations`: existing EF Core migrations only.
- `LaraFashion/wwwroot/css/site.css`: the primary approved site styling.
- `LaraFashion/wwwroot/js/product-image-upload.js`: admin product image upload helper.

## Design Preservation

The current design is approved and must be preserved. Do not redesign the UI, replace styling, change layout structure, alter colors, resize elements, rename classes, or change visible text unless the user explicitly asks for that exact change.

Do not replace or delete `site.css` as a whole unless explicitly requested. Treat it as production styling with many existing dependencies.

Before changing any CSS, inspect the actual Razor markup and class names currently used by the target page or component.

Do not write broad CSS that can affect unrelated pages or components. Avoid generic unscoped selectors such as:

- `button`
- `select`
- `input`
- `form`
- `img`
- `.card`
- `.modal`

Any new CSS must be scoped to the specific page, component, or existing class being changed. Preserve the existing responsive behavior on desktop and mobile.

Product images should remain as fully visible as possible. Do not crop product images or change `object-fit` behavior unless the user explicitly asks.

## Protected Behavior

Preserve all existing behavior for:

- Products, sizes, and inventory quantities.
- Multiple product categories.
- Discounts, including fixed sale price discounts.
- Persisted cart storage.
- Orders and order status transitions.
- Product image upload, conversion, compression, storage, and cleanup.

When editing an existing product, do not create a new product as a side effect. Keep edit paths distinct from create paths.

Do not save data immediately when a checkbox or select value changes. Checkbox and select changes should update temporary UI state only; persistence should happen from an explicit save button unless the user asks for different behavior.

Do not change the database schema, create a migration, edit existing migrations, or modify database files unless there is a real requirement and the user has been told why it is needed.

Do not change discounts, cart, orders, images, categories, product deletion rules, inventory validation, or order cancellation quantity restoration unless the task explicitly targets that behavior.

## Work Process

Before every code change:

1. Read the directly related files.
2. Identify the root cause.
3. State a short plan.
4. Modify the fewest files possible.

After every code change:

1. Run a build.
2. List the modified files.
3. Explain exactly what changed.
4. Mention any risks, deployment steps, or migrations required.

Do not install or update NuGet packages without explicit permission.

Do not touch secrets, connection settings, deployment files, GitHub Actions, or production paths unless explicitly requested.

Do not modify files outside the requested scope. If the requirement is unclear, stop and ask for clarification instead of guessing.

Do not commit or push automatically. The user performs commits and pushes.
