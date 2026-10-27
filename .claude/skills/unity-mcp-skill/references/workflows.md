# Unity-MCP Workflow Patterns

Load only the reference for the current task. Procedures retain prerequisites, verification, and cleanup; anchors preserve links from the original guide. Examples are templates; confirm the installed tool/resource contracts and project prerequisites before use. Reading or editing these documents does not initiate runtime work.

## Table of Contents

- [Setup & Verification](#setup--verification)
- [Scene Generator Build Workflow](#scene-generator-build-workflow)
- [Scene Creation Workflows](#scene-creation-workflows)
- [Script Development Workflows](#script-development-workflows)
- [Asset Management Workflows](#asset-management-workflows)
- [Testing Workflows](#testing-workflows)
- [Debugging Workflows](#debugging-workflows)
- [UI Creation Workflows](#ui-creation-workflows)
- [Input System: Old vs New](#input-system-old-vs-new)
- [Camera & Cinemachine Workflows](#camera--cinemachine-workflows)
- [ProBuilder Workflows](#probuilder-workflows)
- [Graphics & Rendering Workflows](#graphics--rendering-workflows)
- [Package Management Workflows](#package-management-workflows)
- [Package Deployment Workflows](#package-deployment-workflows)
- [API Verification Workflows](#api-verification-workflows)
- [Batch Operations](#batch-operations)
- [Error Recovery Patterns](#error-recovery-patterns)

For shared completion evidence and lifecycle gates, see [validation-workflow.md](validation-workflow.md), [evidence-and-selection.md](evidence-and-selection.md), and [lifecycle-and-recovery.md](lifecycle-and-recovery.md).

## Setup & Verification

Read [workflow-setup.md](workflow-setup.md#setup--verification).

- <a id="initial-connection-verification"></a>[Initial Connection Verification](workflow-setup.md#initial-connection-verification)
- <a id="before-any-operation"></a>[Before Any Operation](workflow-setup.md#before-any-operation)

## Scene Generator Build Workflow

Read [workflow-scene-assets.md](workflow-scene-assets.md#scene-generator-build-workflow).

- <a id="fresh-scene-before-building"></a>[Fresh Scene Before Building](workflow-scene-assets.md#fresh-scene-before-building)
- <a id="wiring-object-references-between-components"></a>[Wiring Object References Between Components](workflow-scene-assets.md#wiring-object-references-between-components)
- <a id="physics-requirements-for-trigger-based-interactions"></a>[Physics Requirements for Trigger-Based Interactions](workflow-scene-assets.md#physics-requirements-for-trigger-based-interactions)
- <a id="script-overwrites-with-replace_class"></a>[Script Overwrites with `replace_class`](workflow-scripts.md#script-overwrites-with-replace_class)
- <a id="script-overwrites-with-manage_scriptactionupdate"></a>[Legacy script overwrite link](workflow-scripts.md#script-overwrites-with-replace_class)

## Scene Creation Workflows

Read [workflow-scene-assets.md](workflow-scene-assets.md#scene-creation-workflows).

- <a id="create-complete-scene-from-scratch"></a>[Create Complete Scene from Scratch](workflow-scene-assets.md#create-complete-scene-from-scratch)
- <a id="populate-scene-with-grid-of-objects"></a>[Populate Scene with Grid of Objects](workflow-scene-assets.md#populate-scene-with-grid-of-objects)
- <a id="clone-and-arrange-objects"></a>[Clone and Arrange Objects](workflow-scene-assets.md#clone-and-arrange-objects)

## Script Development Workflows

Read [workflow-scripts.md](workflow-scripts.md#script-development-workflows).

- <a id="create-new-script-and-attach"></a>[Create New Script and Attach](workflow-scripts.md#create-new-script-and-attach)
- <a id="edit-existing-script-safely"></a>[Edit Existing Script Safely](workflow-scripts.md#edit-existing-script-safely)
- <a id="add-method-to-existing-class"></a>[Add Method to Existing Class](workflow-scripts.md#add-method-to-existing-class)

## Asset Management Workflows

Read [workflow-scene-assets.md](workflow-scene-assets.md#asset-management-workflows).

- <a id="create-and-apply-material"></a>[Create and Apply Material](workflow-scene-assets.md#create-and-apply-material)
- <a id="create-procedural-texture"></a>[Create Procedural Texture](workflow-scene-assets.md#create-procedural-texture)
- <a id="organize-assets-into-folders"></a>[Organize Assets into Folders](workflow-scene-assets.md#organize-assets-into-folders)
- <a id="search-and-process-assets"></a>[Search and Process Assets](workflow-scene-assets.md#search-and-process-assets)
- <a id="instantiate-prefab-in-scene"></a>[Instantiate Prefab in Scene](workflow-scene-assets.md#instantiate-prefab-in-scene)

## Testing Workflows

Read [workflow-testing.md](workflow-testing.md#testing-workflows).

- <a id="run-specific-tests"></a>[Run Specific Tests](workflow-testing.md#run-specific-tests)
- <a id="run-tests-by-category"></a>[Run Tests by Category](workflow-testing.md#run-tests-by-category)
- <a id="test-driven-development-pattern"></a>[Test-Driven Development Pattern](workflow-testing.md#test-driven-development-pattern)

## Debugging Workflows

Read [workflow-debugging.md](workflow-debugging.md#debugging-workflows).

- <a id="diagnose-compilation-errors"></a>[Diagnose Compilation Errors](workflow-debugging.md#diagnose-compilation-errors)
- <a id="investigate-missing-references"></a>[Investigate Missing References](workflow-debugging.md#investigate-missing-references)
- <a id="check-scene-state"></a>[Check Scene State](workflow-debugging.md#check-scene-state)

## UI Creation Workflows

Read [workflow-ui-toolkit.md](workflow-ui-toolkit.md#ui-creation-workflows).

- <a id="step-0-detect-project-ui-capabilities"></a>[Step 0: Detect Project UI Capabilities](workflow-ui-toolkit.md#step-0-detect-project-ui-capabilities)
- <a id="ui-toolkit-workflows-manage_ui"></a>[UI Toolkit Workflows (manage_ui)](workflow-ui-toolkit.md#ui-toolkit-workflows-manage_ui)
- <a id="create-a-complete-ui-screen"></a>[Create a Complete UI Screen](workflow-ui-toolkit.md#create-a-complete-ui-screen)
- <a id="update-existing-ui"></a>[Update Existing UI](workflow-ui-toolkit.md#update-existing-ui)
- <a id="custom-panelsettings"></a>[Custom PanelSettings](workflow-ui-toolkit.md#custom-panelsettings)
- <a id="ugui-canvas-based-workflows"></a>[uGUI (Canvas-Based) Workflows](workflow-ugui-foundation.md#ugui-canvas-based-workflows)
- <a id="recttransform-sizing-critical-for-all-ui-children"></a>[RectTransform Sizing (Critical for All UI Children)](workflow-ugui-foundation.md#recttransform-sizing-critical-for-all-ui-children)
- <a id="create-canvas-foundation-for-all-ui"></a>[Create Canvas (Foundation for All UI)](workflow-ugui-foundation.md#create-canvas-foundation-for-all-ui)
- <a id="create-eventsystem-required-once-per-scene-for-ui-interaction"></a>[Create EventSystem (Required Once Per Scene for UI Interaction)](workflow-ugui-foundation.md#create-eventsystem-required-once-per-scene-for-ui-interaction)
- <a id="create-panel-background-container"></a>[Create Panel (Background Container)](workflow-ugui-foundation.md#create-panel-background-container)
- <a id="create-text-textmeshpro"></a>[Create Text (TextMeshPro)](workflow-ugui-foundation.md#create-text-textmeshpro)
- <a id="create-button-with-label"></a>[Create Button (With Label)](workflow-ugui-foundation.md#create-button-with-label)
- <a id="create-slider-with-reference-wiring"></a>[Create Slider (With Reference Wiring)](workflow-ugui-controls.md#create-slider-with-reference-wiring)
- <a id="create-input-field-with-reference-wiring"></a>[Create Input Field (With Reference Wiring)](workflow-ugui-controls.md#create-input-field-with-reference-wiring)
- <a id="create-toggle-with-reference-wiring"></a>[Create Toggle (With Reference Wiring)](workflow-ugui-controls.md#create-toggle-with-reference-wiring)
- <a id="add-layout-group-verticalhorizontalgrid"></a>[Add Layout Group (Vertical/Horizontal/Grid)](workflow-ugui-foundation.md#add-layout-group-verticalhorizontalgrid)
- <a id="complete-example-main-menu-screen"></a>[Complete Example: Main Menu Screen](workflow-ugui-foundation.md#complete-example-main-menu-screen)
- <a id="ui-component-quick-reference"></a>[UI Component Quick Reference](workflow-ugui-foundation.md#ui-component-quick-reference)

## Input System: Old vs New

Read [workflow-input.md](workflow-input.md#input-system-old-vs-new).

- <a id="detection"></a>[Detection](workflow-input.md#detection)
- <a id="eventsystem--old-input-manager"></a>[EventSystem — Old Input Manager](workflow-input.md#eventsystem--old-input-manager)
- <a id="eventsystem--new-input-system"></a>[EventSystem — New Input System](workflow-input.md#eventsystem--new-input-system)
- <a id="when-activeinputhandler-is-both"></a>[When `activeInputHandler` is `"Both"`](workflow-input.md#when-activeinputhandler-is-both)

## Camera & Cinemachine Workflows

Read [workflow-camera-modeling.md](workflow-camera-modeling.md#camera--cinemachine-workflows).

- <a id="setting-up-a-third-person-camera"></a>[Setting Up a Third-Person Camera](workflow-camera-modeling.md#setting-up-a-third-person-camera)
- <a id="multi-camera-setup-with-blending"></a>[Multi-Camera Setup with Blending](workflow-camera-modeling.md#multi-camera-setup-with-blending)
- <a id="camera-without-cinemachine"></a>[Camera Without Cinemachine](workflow-camera-modeling.md#camera-without-cinemachine)
- <a id="camera-inspection-workflow"></a>[Camera Inspection Workflow](workflow-camera-modeling.md#camera-inspection-workflow)
- <a id="scene-view-screenshot-workflow"></a>[Scene View Screenshot Workflow](workflow-camera-modeling.md#scene-view-screenshot-workflow)

## ProBuilder Workflows

Read [workflow-camera-modeling.md](workflow-camera-modeling.md#probuilder-workflows).

- <a id="probuilder-vs-primitives-decision"></a>[ProBuilder vs Primitives Decision](workflow-camera-modeling.md#probuilder-vs-primitives-decision)
- <a id="basic-probuilder-scene-build"></a>[Basic ProBuilder Scene Build](workflow-camera-modeling.md#basic-probuilder-scene-build)
- <a id="edit-verify-loop-pattern"></a>[Edit-Verify Loop Pattern](workflow-camera-modeling.md#edit-verify-loop-pattern)
- <a id="known-limitations"></a>[Known Limitations](workflow-camera-modeling.md#known-limitations)

## Graphics & Rendering Workflows

Read [workflow-graphics.md](workflow-graphics.md#graphics--rendering-workflows).

- <a id="setting-up-post-processing"></a>[Setting Up Post-Processing](workflow-graphics.md#setting-up-post-processing)
- <a id="adding-a-full-screen-effect-via-renderer-features-urp"></a>[Adding a Full-Screen Effect via Renderer Features (URP)](workflow-graphics.md#adding-a-full-screen-effect-via-renderer-features-urp)
- <a id="configuring-light-baking"></a>[Configuring Light Baking](workflow-graphics.md#configuring-light-baking)

## Package Management Workflows

Read [workflow-packages-api.md](workflow-packages-api.md#package-management-workflows).

- <a id="install-a-package-and-verify"></a>[Install a Package and Verify](workflow-packages-api.md#install-a-package-and-verify)
- <a id="add-openupm-registry-and-install-package"></a>[Add OpenUPM Registry and Install Package](workflow-packages-api.md#add-openupm-registry-and-install-package)
- <a id="safe-package-removal"></a>[Safe Package Removal](workflow-packages-api.md#safe-package-removal)
- <a id="install-from-git-url-eg-nugetforunity"></a>[Install from Git URL (e.g., NuGetForUnity)](workflow-packages-api.md#install-from-git-url-eg-nugetforunity)

## Package Deployment Workflows

Read [workflow-packages-api.md](workflow-packages-api.md#package-deployment-workflows).

- <a id="iterative-development-loop-edit--deploy--test"></a>[Iterative Development Loop (Edit → Deploy → Test)](workflow-packages-api.md#iterative-development-loop-edit--deploy--test)
- <a id="rollback-after-failed-deploy"></a>[Rollback After Failed Deploy](workflow-packages-api.md#rollback-after-failed-deploy)

## API Verification Workflows

Read [workflow-packages-api.md](workflow-packages-api.md#api-verification-workflows).

- <a id="full-api-verification-before-writing-code"></a>[Full API Verification Before Writing Code](workflow-packages-api.md#full-api-verification-before-writing-code)
- <a id="batch-api-lookup"></a>[Batch API Lookup](workflow-packages-api.md#batch-api-lookup)
- <a id="finding-shaders-and-materials-in-project"></a>[Finding Shaders and Materials in Project](workflow-packages-api.md#finding-shaders-and-materials-in-project)
- <a id="manual-and-package-documentation"></a>[Manual and Package Documentation](workflow-packages-api.md#manual-and-package-documentation)
- <a id="verifying-apis-across-unity-versions"></a>[Verifying APIs Across Unity Versions](workflow-packages-api.md#verifying-apis-across-unity-versions)

## Batch Operations

Read [workflow-batch-recovery.md](workflow-batch-recovery.md#batch-operations).

- <a id="batch-discovery-multi-search"></a>[Batch Discovery (Multi-Search)](workflow-batch-recovery.md#batch-discovery-multi-search)
- <a id="mass-property-update"></a>[Mass Property Update](workflow-batch-recovery.md#mass-property-update)
- <a id="mass-object-creation-with-variations"></a>[Mass Object Creation with Variations](workflow-batch-recovery.md#mass-object-creation-with-variations)
- <a id="cleanup-pattern"></a>[Cleanup Pattern](workflow-batch-recovery.md#cleanup-pattern)

## Error Recovery Patterns

Read [workflow-batch-recovery.md](workflow-batch-recovery.md#error-recovery-patterns).

- <a id="stale-file-recovery"></a>[Stale File Recovery](workflow-batch-recovery.md#stale-file-recovery)
- <a id="domain-reload-recovery"></a>[Domain Reload Recovery](workflow-batch-recovery.md#domain-reload-recovery)
- <a id="compilation-block-recovery"></a>[Compilation Block Recovery](workflow-batch-recovery.md#compilation-block-recovery)
