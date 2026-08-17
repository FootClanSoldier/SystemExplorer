# System Explorer – Deep Research: assembly-rebuild-dependent ScriptEditor/autocomplete stability

## Executive summary och evidensbedömning

Den här undersökningen använder **System Explorer `main` vid commit `0c198e3fc4699ce2c7bac3bcd846d6ca8fe3c061`** som enda source of truth för den aktuella pluginimplementationen. Den verifierade implementationen ligger under `addons/system_explorer/Scripts`; `PartialMap` och `LifecycleRoadmap` ligger i den aktuella repostrukturen under `addons/system_explorer/Scripts/docs`, inte i reporoten. PartialMap beskriver den nuvarande ansvarsfördelningen, medan roadmapen uttryckligen behandlas som strategisk kontext och inte som bevis för runtime-behavior. fileciteturn66file0L1-L2 Godot-sidan har analyserats mot taggen **`4.6.3-stable`**, som pekar på commit `35e80b3a8822a9df9be390814b62f44c0a9c69e8`. fileciteturn20file0L1-L12

**Min huvudbedömning är att den mest sannolika problemklassen just nu är en managed/native handoff-defekt över assembly reload: en ny System Explorer-generation återtar ScriptEditor/CodeEdit-authority över native editorobjekt som har överlevt den gamla managed generationen, utan en explicit post-reload-gräns som bevisar att den överlevande editorn är lifecycle-stabil.** Detta är **Arkitekturell inferens**, inte en verifierad crash-stack. Den starkaste konkreta observationen är att crashsessionen faktiskt återbinder den nya managed generationen till samma native `CodeEdit`-instance ID som bar System Explorer-state från den gamla generationen. fileciteturn8file3

Evidensen att **assembly rebuild är den centrala möjliggörande faktorn** är stark, men den är inte tillräcklig för att kalla rebuild i sig root cause. `log_41_NoCrash.log` genomför ungefär **4 020 `EditScript`-operationer** utan någon `AssemblyLoadContext.Unloading`; `log_43_Crash.log` hard-crashar efter en verifierad managed generation transition trots att den endast hinner ungefär **2 740 `EditScript`-operationer**. Därmed är navigation count, antalet `EditScript`-calls och ren långvarig ScriptEditor-belastning sämre förklaringar än rebuild-handoff. fileciteturn9file0 fileciteturn8file0

Samtidigt är det viktigt att inte överläsa crash-tailen. I crashsessionen går den instrumenterade `AutocompletePluginHost.EnsureLifecycleCurrent(...)` upprepade gånger hela vägen genom `EditorBinding.Begin`, `ProjectIndexLifecycle.Begin`, `FirstDrain.Begin`, `PostFirstDrainFeatureGates.Begin`, `SecondDrain.Begin` och `Returned`, även **efter** assembly reload. En post-reload rebind går dessutom vidare till `HandleScriptChanged.Returned`, `LifecycleState='Stable'` och en ny `BindingEpoch`. fileciteturn8file3 `EnsureLifecycleCurrent()` är därför **inte verifierad root cause**, och varken `AutocompleteEditorBinding` eller project-index lifecycle bör pekas ut som crashplats utan ytterligare native evidens.

Den viktigaste source-verifierade förklaringen till varför en managed call kan returnera och ändå ha initierat något som spelar roll senare är att Godots editor och textkomponenter själva har **deferred native/main-thread lifecycle**. `EditorInterface::edit_script()` går direkt in i `ScriptEditor::edit()`. fileciteturn22file0 ScriptEditors history/editor-switching-kod defererar arbete kring caret/history och emitterar sedan script-change-notifikationer, medan `TextEdit` explicit gör `_emit_text_changed()` via deferred callable. fileciteturn24file1 fileciteturn41file1 Detta gör modellen

```text
managed call Returned
≠
alla native editor-state-transitions som callen initierade är färdiga
```

source-verifierad.

**Primär rekommendation: B. Inför en explicit managed-reload quiescence/stabilization barrier som en correctness-refactor, före den bredare planerade CodeEdit mutation/transaction coordinatorn.** Coordinatorn är fortfarande en bra senare komponent, men serialization av mutationer kan inte i sig göra ett osäkert reload-fönster säkert. Den måste konsumera ett separat, starkare beslut om att den nya managed generationen faktiskt får börja använda den överlevande ScriptEditor/CodeEdit-miljön.

Den mest konkreta förändringen vid rebuild som bäst förklarar skillnaden är därför:

> **System Explorers managed graph byts ut — host, callbacks, leases, wrapperrelationer och generation-owned state — medan Godots native editorprocess och åtminstone vissa CodeEdit-objekt fortsätter existera. Den nya generationen kopplar sedan upp sig mot dessa redan historiska native objekt. Nuvarande `ManagedAssemblyGeneration`, `HostInstanceToken`, `ScriptTransitionId` och `BindingEpoch` skyddar mycket väl mot stale managed authority, men det saknas en explicit dimension för “native editor state har stabiliserats efter reload och är nu åter muterbart”.**

Detta är den lucka jag skulle behandla som nästa correctness-problem.

## A/B-analys och falsifiering

De två loggarna är ett ovanligt användbart experiment eftersom belastningen är likartad medan assembly-generation behavior skiljer sig fundamentalt. Navigation Stress på `main` använder en run token och `ManagedAssemblyGeneration`, utför högst en selection per `_Process`-iteration, använder en 75 ms cadence och driver vanlig Tree-selection/navigation; reload reset:ar en gammal run och rearm sker först i den nya generationen. Det är därför främst en accelerator för samma navigation path, inte en separat direkt ScriptEditor-API-hammer. fileciteturn34file0L1-L7

| Observation | `log_41_NoCrash.log` | `log_43_Crash.log` | Tolkning |
|---|---:|---:|---|
| `EditScript` Begin | ca **4 020** träffar | ca **2 740** träffar | Crashsessionen har **färre**, inte fler, navigationer. fileciteturn9file0 fileciteturn8file0 |
| `EditScript` Return | matchar Begin i de undersökta flödena | matchar Begin i de undersökta flödena | Ingen evidens för att hard crash sker synkront inne i System Explorers `EditScript`-call boundary. fileciteturn9file3 fileciteturn8file1 |
| `EnsureLifecycleCurrent.Returned` | ca **4 024** | ca **2 746** | Lifecycle ensure återkommer framgångsrikt tusentals gånger och även efter reload. fileciteturn9file0 fileciteturn8file0 |
| ALC unload | **ingen** observerad | **en tydlig** generation transition | Starkaste A/B-differentiatorn. |
| Managed generation | en generation | `eadf…` → `f603…` | Hela managed ownership-domänen byts. fileciteturn8file3 |
| Autocomplete host | Host token `1` | `1` → ny host token `2` | System Explorer skapar ny host-authority efter reload. fileciteturn8file1 |
| Project-index workers vid unload | ingen unload | `ActiveWorkers=0`, inga worker kinds, drain utan kvarvarande workers | Talar starkt mot “aktiv Roslyn/project-index worker kraschar direkt under unload”. |
| Native CodeEdit över generation | ej relevant | samma native ID `2322939874938` upptäcks som orphan från gamla generationen och återvinns | Direkt bevis att autocomplete återtar ett native editorobjekt med pre-reload-historia. fileciteturn8file3 |
| Termination | Navigation Stress når minst checkpoint 4 000 utan hard crash | abrupt loggslut | Ren navigation uthärdas längre utan reload. fileciteturn9file0 |
| Crash-tail | fortsatt normal lifecycle | sista observerade managed TextChanged-validation returnerar | Sista managed callback är inte automatiskt crash-stack. |

Navigation Stress loggar var hundrade activation; no-crash-loggen innehåller 40 sådana progressposter och 4 020 `EditScript`-calls. Redan tidigt syns exempelvis activations 100, 200, … 1 000 och vidare mot tusentals navigationer, med `EditScript Returned` och efterföljande lifecycle ensure. fileciteturn9file0 fileciteturn9file8 Det är **Loggstödd** evidens mot hypotesen att det helt enkelt finns en låg deterministisk “navigationer före crash”-tröskel.

Crashloggen gör den centrala kontrasten mycket skarpare. Före reload ligger managed generation på `eadf5115…`; efter reload ligger den på `f603a6b5…`, `HostInstanceToken='2'`, och den nya hosten genomför en full deferred rebind. Under den första post-reload-bindningen upptäcker System Explorer att `CodeEditNativeInstanceId='2322939874938'` fortfarande har ownership metadata från `PreviousManagedAssemblyGeneration='eadf…'`. Den återställer orphaned prefix/theme-ownership och binder därefter samma native CodeEdit under den nya generationen. fileciteturn8file3 Det är **Source-verifierad + Loggstödd** evidens för att “native editorobjekt överlever managed generation” inte bara är en abstrakt möjlighet i detta fall.

Efter reload fortsätter dessutom navigationen länge. Exempelvis returnerar `EditScript` normalt för AppRoot, SoundCoordinator, SoundDefinition, SoundLibrary, SoundPlayback, SoundPlayerPool och senare scripts; varje transition kan följas av deferred ensure och ny stable binding. fileciteturn8file1 fileciteturn8file6 Därför är modellen **inte** “första accessen till gammal CodeEdit efter reload kraschar”. Den bättre modellen är att reload skapar ett **svagare lifecycle-tillstånd eller en extra race/state-dimension**, som sedan stressas av fortsatt navigation/autocomplete.

Även `ScriptTransitionId`-mönstret är viktigt. I steady state kan en System Explorer-navigation ge den förväntade `editor_script_changed`-observationen och därefter ytterligare observerade script-targets som supersedar transitionen. No-crash-loggen visar sådana snabba transitioner och klarar dem ändå. fileciteturn9file1 Detta försvagar teorin att varje supersede/reentrant script-change i sig är defekten. Däremot visar det att ScriptEditor-navigation redan i en frisk generation är en **state transition**, inte bara ett funktionsanrop.

### Vad A/B-testet faktiskt falsifierar eller försvagar

**Navigation count ensam — kraftigt försvagad.** No-crash-sessionen genomför ungefär 47 % fler `EditScript`-calls än crashsessionen. En enkel ackumulerande räknare är därför en dålig huvudförklaring. fileciteturn9file0 fileciteturn8file0

**Synchronous `EditorInterface.EditScript()` crash — kraftigt försvagad.** System Explorers kod loggar Begin före anropet och Returned efter det. I både A och B returnerar navigationerna regelbundet, även post-reload. fileciteturn16file0L1-L7 fileciteturn8file1 Det utesluter inte att `EditScript` startar state som kraschar senare; det utesluter bara den enkla modellen att managed call-stack fastnar i själva callen.

**`EnsureLifecycleCurrent()` som generell root cause — kraftigt försvagad.** Den auktoriserade progressionen återkommer genom samtliga instrumenterade faser och `Returned` även efter reload. fileciteturn8file3

**Project-index worker aktiv under unload — kraftigt försvagad.** Crashloggen anger noll aktiva workers vid unload och worker-drain lämnar inget aktivt arbete. Det utesluter inte index-lifecycle-fel generellt, men det finns inget loggstöd för att en aktiv worker är den direkta unload-crashmekanismen.

**TextChanged ensam — försvagad.** TextChanged-validation förekommer och returnerar normalt i no-crash-sessionen; det förekommer även efter reload. Godots egen `TextEdit` defererar `text_changed`, så callbacken är relevant som state-boundary, men det finns ingen evidens för att “TextChanged => crash” isolerat. fileciteturn41file1

**Automatic using ensam — försvagad.** Den aktiva implementationen gör completion-confirmation och den sekundära `InsertText`-mutationen i olika lifecycle-faser; samma funktion används framgångsrikt många gånger. Den kan fortfarande bidra till state pressure, men förklarar inte rebuild-korrelationen ensam. fileciteturn33file0L1-L2

**Navigation Stress själv — kraftigt försvagad.** Harnessen använder generation/run guards, går via vanlig tree selection och uthärdar tusentals navigationer i den negativa kontrollen. fileciteturn34file0L1-L7

Detta lämnar **assembly reload som klart starkaste experimentvariabel**, men mer precist som *enabler för ett nytt ownership/lifecycle-tillstånd* snarare än som bevisad instruktion som kraschar processen.

## Verifierad System Explorer-lifecycle och Godot-native call graph

PartialMap placerar reload-ansvar i `SystemExplorerPlugin.EditorReloadLifecycle.cs`, ScriptEditor/autocomplete-recovery i autocomplete/lifecycle-delarna och gör tydligt att en host från fel managed generation pensioneras medan surviving native UI kan återanvändas. PartialMap anger också att ScriptEditor lifecycle och `BindingEpoch` invalidateras före återtagande av autocomplete-authority. fileciteturn66file0L1-L2 Den faktiska koden på `main` verifierar detta; dokumentet används här bara för ansvarskartan.

### Aktuell System Explorer-call graph

Den relevanta normala navigationen kan sammanfattas så här:

```text
System Explorer Tree activation
    ↓
OpenScriptFromSystemExplorer(...)
    ↓
ScriptEditorLifecycleCoordinator.BeginTransition(
    origin = SystemExplorerNavigation,
    expectedScriptPath = target)
    ↓
EditorInterface.Singleton.EditScript(script)
    ↓
Godot ScriptEditor transition
    ↓
editor_script_changed
    ↓
SystemExplorer ScriptEditorSync / OnAutocompleteScriptChanged
    ↓
lifecycle -> BindingPending
    ↓
named deferred autocomplete ScriptEditor rebind
    ↓
generation + host + operation + transition guards
    ↓
AutocompletePluginHost.EnsureLifecycleCurrent(...)
    ↓
AutocompleteEditorBinding resolves:
    ScriptEditor
    → current Script
    → current ScriptEditorBase
    → BaseEditor
    → CodeEdit
    ↓
CodeEdit TextChanged / CodeCompletionRequested / GuiInput wiring
    ↓
BindingEpoch commit
    ↓
LifecycleState = Stable
```

`OpenScriptFromSystemExplorer` börjar den explicita transitionen innan `EditorInterface.Singleton.EditScript(script)` och håller en diagnostic boundary runt callen. fileciteturn16file0L1-L7 `ScriptEditorLifecycleCoordinator` skiljer på `Detached`, `ScriptTransitionPending`, `BindingPending` och `Stable`; en binding lease innehåller managed generation, host token, transition, binding epoch och native ScriptEditor/ScriptEditorBase/CodeEdit identities. En commit kräver fortfarande aktuell generation/transition och korrekta native IDs. fileciteturn15file0L1-L7

Det här är en genomtänkt förbättring jämfört med att hålla en lös `_codeEdit`-referens och hoppas att den fortsätter vara aktuell. **Source-verifierad styrka:** `ScriptTransitionId` skyddar mot stale script-targets, `BindingEpoch` mot stale leases och `HostInstanceToken` mot callbacks till en pensionerad autocomplete-host.

`OnAutocompleteScriptChanged` gör inte längre full native rebind synkront i `editor_script_changed`-callbacken. Den invalidaterar/reagerar på transitionen och queue:ar en named deferred rebind. `ApplyDeferredAutocompleteScriptChangeRebind` kontrollerar generation, host token, operation token, lifecycle authority, Namespace Refactor-quiescence och andra barriärer före `EnsureLifecycleCurrent` och `HandleScriptChanged`. fileciteturn18file0L1-L2 Detta är en **bra permanent princip**: plugin-owned deferred intent är generation-bound och latest-wins.

### Vad `EditorInterface.EditScript()` faktiskt gör i Godot 4.6.3

I exakt 4.6.3 är implementationen tunn:

```cpp
void EditorInterface::edit_script(
    const Ref<Script> &p_script,
    int p_line,
    int p_col,
    bool p_grab_focus
) {
    ScriptEditor::get_singleton()->edit(
        p_script,
        p_line - 1,
        p_col - 1,
        p_grab_focus
    );
}
```

Detta är direkt **Source-verifierat** i `editor/editor_interface.cpp`. fileciteturn22file0 Det innebär att den viktiga staten finns i `ScriptEditor::edit()` och ScriptEditors interna tab/history/editor machinery, inte i `EditorInterface` själv.

I `editor/script/script_editor_plugin.cpp` finns `ScriptEditor::edit(const Ref<Resource> &, ...)`, som tar den Script-resource som ska öppnas. fileciteturn24file0 ScriptEditor har därefter egen history/current-editor-logik och `notify_script_changed()`, som emitterar `editor_script_changed`. I history/update-flödet kommenterar Godot uttryckligen att återställning av edit state kan ändra caret och att `TextEdit::caret_changed` är deferred; ScriptEditor defererar därför sin egen history-unlock och fortsätter sedan med focus/script-change-notifiering. fileciteturn24file1

Den praktiska konsekvensen är central:

```text
SystemExplorer:
EditScript Begin
  ↓
Godot ScriptEditor::edit(...)
  ↓
en mängd native current-editor/history/focus/state-arbete
  ↓
editor_script_changed kan emitteras
  ↓
managed callbacks körs
  ↓
EditorInterface.EditScript Return
  ↓
andra native/deferred signaler eller valideringar kan fortfarande återstå
```

Det finns alltså **ingen source-baserad grund för att tolka `EditScript Returned` som “ScriptEditor är nu quiescent”**.

### CodeEdit och TextEdit är stateful native komponenter, inte bara text-API:n

System Explorer binder till Godots `CodeEdit` och använder bland annat completion request/confirmation, `TextChanged`, `GuiInput`, prefix/theme override och `InsertText`.

I Godot 4.6.3 kräver `CodeEdit::confirm_code_completion()` att editorn är editable och att `code_completion_active` är sant; implementationen går sedan in i en complex/multicaret edit operation innan den applicerar completion. fileciteturn40file0 fileciteturn40file7 Completion är därmed ett native state machine-flöde med popup/options/selected item/caret/text-ändringar, inte ett rent “returnera vald sträng”-API.

Ännu viktigare för crash-tail-tolkningen är `TextEdit::_text_changed()`. När texten ändras och kontrollen ligger i trädet gör Godot:

```cpp
callable_mp(this, &TextEdit::_emit_text_changed).call_deferred();
```

och först i den senare `_emit_text_changed()` emitteras `text_changed`. fileciteturn41file1 En System Explorer-`InsertText()` kan därför ha returnerat normalt innan den `TextChanged` som operationen genererade körs.

Detta ger en konkret source-verifierad mekanism för användarens distinktion mellan:

> operationen som initierar state transitionen

och

> callback/frame där en senare defekt blir observerbar.

Det gör också att den sista loggade managed callbacken inte kan användas som implicit native stacktrace.

## Assembly-reload-handoff: managed state mot överlevande native state

Det här är undersökningens viktigaste område.

### Vad Godot faktiskt gör med den managed sidan

Godot 4.6.3 laddar editorprojektets C#-kod via en collectible plugin load context. I `GodotPlugins/Main.cs` finns en explicit `UnloadProjectPlugin`/`UnloadPlugin`-väg; Godot anropar `Unload()` och använder weak-reference/GC-loop för att låta det collectible load context:et försvinna. fileciteturn57file0L1-L7

På native C#-script-sidan visar `modules/mono/csharp_script.cpp` att reload är mer sofistikerad än “släpp alla delegates och starta om”. Godot bedömer vilka script instances som är reloadable och har till och med en kommentar om att inte reload:a scripts med enbart non-collectible instances för att undvika att bland annat event subscriptions störs. fileciteturn48file0 Före reload serialiserar Godot managed callables; efter reload deserialiseras dessa innan scriptens interna state återställs. fileciteturn48file0

Detta är viktigt för signalhypotesen: **Godot har explicit machinery för att hantera managed callable-state över hot reload.** En teori om “alla gamla signaler blir automatiskt dangling funktionspekare efter ALC unload” är därför för grov och inte source-stödd.

System Explorer använder dessutom huvudsakligen `new Callable(this, methodName)` för reload-safe editor signals. I GodotSharp `Callable` är target+method-konstruktorn en annan representation än den delegate/trampoline-baserade callable-formen; target och method lagras, medan delegate/trampoline är null i den konstruktionen. fileciteturn64file0L1-L7 Det gör det ännu viktigare att inte klumpa ihop System Explorers named method connections med Godots managed delegate serialization.

### Vad som faktiskt överlever på native sidan

Crashloggen ger här det starkaste direkta beviset.

Före reload är den gamla generationen:

```text
ManagedAssemblyGeneration = eadf5115b5b44851a2a36335e66f5f80
HostInstanceToken         = 1
```

Efter reload är den nya:

```text
ManagedAssemblyGeneration = f603a6b5d6cf498bb4dc9e05beb9eb8d
HostInstanceToken         = 2
```

När den nya hosten gör sin första recovery/rebind hittar `AutocompleteCodeEditNativeOwnershipBridge` fortfarande metadata på:

```text
CodeEditNativeInstanceId = 2322939874938
PreviousManagedAssemblyGeneration = eadf...
CurrentManagedAssemblyGeneration  = f603...
```

och loggar först `native CodeEdit ownership orphan detected`, därefter `... orphan recovered`. fileciteturn8file3

Detta är **Loggstödd** evidens för den centrala modellen:

```text
gammal System Explorer managed generation
    ↓ unload

native CodeEdit 2322939874938
    ↓ fortsätter leva och bär pluginägd metadata/state

ny System Explorer managed generation
    ↓
ny host
    ↓
upptäcker gammal native ownership
    ↓
återställer/adopterar samma native CodeEdit
```

`AutocompleteCodeEditNativeOwnershipBridge` är alltså inte bara defensiv kod för en teoretisk situation; crashloggen visar att den recovery-situationen faktiskt inträffar. Bridge-markern håller bland annat managed-generation ownership och tidigare CodeEdit prefix/theme state. fileciteturn38file0

Detta är samtidigt en **bra** arkitekturprincip och ett varningstecken. Det bra är att System Explorer inte antar att managed state är den enda sanningen; den har en native ownership ledger som låter en ny generation städa presentation state efter den gamla. Det riskabla är att “jag har framgångsrikt städat den gamla generationens metadata på det här native objektet” inte är samma sak som “det här native objektet och dess ScriptEditor-omgivning har nu nått en stabil lifecycle boundary”.

### Managed wrapper identity är inte native lifecycle readiness

GodotSharp 4.6.3 definierar `GodotObject.IsInstanceValid()` i praktiken som:

```csharp
return instance != null && instance.NativeInstance != IntPtr.Zero;
```

fileciteturn62file0L1-L7

Det är en mycket smalare garanti än vad autocomplete behöver. Den säger ungefär att managed wrappern fortfarande har en native pointer; den säger inte att:

- objektet fortfarande är aktuell ScriptEditorBase för current script,
- editor history/focus/validation är färdig,
- CodeEdit är utanför en completion transition,
- inga deferred TextEdit-signaler väntar,
- ingen tab/editor replacement pågår,
- det är säkert att koppla om signaler eller mutera completion state just i denna frame.

System Explorer gör mer än bara `IsInstanceValid()` — `AutocompleteEditorBinding` jämför current script/current editor/base editor/native instance IDs och lease-data — vilket är bra. fileciteturn25file0L1-L2 Men även den starkare kontrollen bevisar **identity/currentness**, inte **quiescence**.

Godots runtime interop har också en uttrycklig path för att skapa managed binding/wrapper runt ett existerande unmanaged `Object`, inklusive `godotsharp_internal_unmanaged_instance_binding_create_managed(Object *p_unmanaged, ...)`. fileciteturn54file0 Det är source-evidens för att managed binding och native object lifetime är separata lager. Jag skulle däremot inte gå längre och påstå att crashloggen bevisar att just `CodeEdit 2322939874938` fick två specifika wrapperobjekt med olika managed object identity; loggen bevisar native-identiteten, inte managed wrapper-referensidentiteten.

### Signal reconnect är ett riskområde, men inte huvudmisstanken

System Explorers reload-recovery använder named callable helpers med ungefär modellen `IsConnected` → `Connect`, och motsvarande defensive disconnect. `EditorReloadLifecycle` försöker dessutom återanvända en giltig befintlig dock/editorintegration innan full rebuild. fileciteturn27file0L1-L7

Upstream visar att C# signals över editor builds historiskt har verkliga reloadproblem. Godot issue #102455 är en bekräftad `.NET`/buildsystem-bugg där en editor Tool-resource signal efter rebuild blir disconnected på ett sätt som senare kan ge fel vid disconnect; upprepade builds kan leda vidare till disposed-object/unload-problem. fileciteturn58file0L3-L44 Issue #84394 beskriver repeated event subscriptions efter rebuild i C# `[Tool]`-resurser. fileciteturn59file0L3-L35

Detta gör **signal ownership över reload till ett legitimt riskområde**. Men det finns tre saker som talar mot att kalla det den ledande root-cause-teorin för System Explorer:

För det första har Godot explicit callable reload machinery. För det andra använder System Explorer named method callables snarare än rena delegate callables för mycket av editorintegrationen. För det tredje visar System Explorer-loggen inga tydliga duplicate-signal/disconnect-fel före hard crash. Därför klassificerar jag signalteorin som **Upstream-stödd risk + Arkitekturell inferens**, inte verifierad crashmekanism.

### Deferred work har två olika ownership-domäner

System Explorer gör mycket rätt med sina egna deferred paths. `ManagedAssemblyGeneration`, `HostInstanceToken`, operation tokens och `ScriptTransitionId` gör att en gammal managed operation normalt ska dö när generation eller host byts. fileciteturn18file0L1-L2

Men dessa guards skyddar endast **System Explorers egen intent**. De kan inte retroaktivt lägga en generation token på Godots egna native deferred callables.

Godots `TextEdit` queue:ar exempelvis `_emit_text_changed` internt. fileciteturn41file1 ScriptEditors caret/history path innehåller också deferred ordering. fileciteturn24file1 `Object::call_deferred`/MessageQueue använder i native lagret Object/method-baserad queueing, inte System Explorers `ManagedAssemblyGeneration`. Därför finns en viktig asymmetri:

```text
SystemExplorer-owned deferred work:
    generation/token guards finns

Godot-owned editor deferred work:
    känner inte till SystemExplorer-generationen
```

Det är precis därför en **reload quiescence/stabilization barrier** är mer fundamental än ytterligare tokens på enskilda System Explorer-callbacks.

## Riskinventering och försvagade hypoteser

Nedan är de viktigaste mönstren rangordnade efter hur väl de både passar source och förklarar rebuild-beroendet. Ingen av dem är presenterad som verifierad native crash-stack.

| Prioritet | System Explorer-callsite | Godot 4.6.3-motsvarighet | Risk och evidens | Permanent förbättring |
|---|---|---|---|---|
| **Högst** | `EditorReloadLifecycle` + `AutocompleteEditorBinding.Resolve...` + `AutocompleteCodeEditNativeOwnershipBridge` | `ScriptEditor`, `CodeEdit`, GodotObject/native binding | Ny managed generation återtar samma överlevande native CodeEdit. Det finns generation/identity-guards men ingen explicit post-reload native-stability epoch. **Source-verifierad + Loggstödd + Arkitekturell inferens.** fileciteturn8file3 | Explicit reload quiescence/stabilization barrier; en native binding får inte commit:as som muterbar bara för att identity/path är current. |
| **Hög** | `AutocompleteEditorBinding` och project-type confirmation/automatic using | `CodeEdit::confirm_code_completion`, `TextEdit::_text_changed` | Managed autocomplete state kan reset:as medan native completion/text state fortsätter existera. Normal rebind har diagnostiskt `cancelNativeCompletionOnRebind=false`; `TextChanged` är deferred. **Source-verifierad risk**, inte root cause. fileciteturn40file0 fileciteturn41file1 | Låt framtida mutation authority äga all completion/text mutation och tillåta den först efter reload-ready lease. |
| **Hög–medel** | Recovery som återanvänder befintlig editorintegration | Godot native editor tree + managed wrapper/binding | “Objektet finns” kan misstas för “objektet är lifecycle-stabilt”. `IsInstanceValid` är bara nonzero native pointer. **Source-verifierad.** fileciteturn62file0L1-L7 | Separera native existence, current identity och stabilization readiness i tre olika predicates. |
| **Medel** | Named signal connect/disconnect i `EditorReloadLifecycle` och `AutocompleteEditorBinding` | `Object::connect`, `Callable`, C# reload callable handling | Signal reconnect över C# editor rebuild har upstreamproblem; duplicate/stale identity är möjligt men inte observerat här. **Source-verifierad + Upstream-stödd risk.** fileciteturn58file0L3-L44 | Signal ownership bör ingå i lifecycle activation/deactivation och vara generation-scoped, men inte behandlas som nuvarande verifierade crashorsak. |
| **Medel** | Alla plugin-owned deferred rebind/using/validation paths | Godot `MessageQueue`, ScriptEditor/TextEdit deferred work | System Explorer guards skyddar egna deferred callbacks men inte native arbete Godot redan queue:at. **Source-verifierad + Arkitekturell inferens.** fileciteturn41file1 | Barrier som låter native queue/state nå ny stabil observation innan mutation återaktiveras. |
| **Låg för denna crash** | Project index lifecycle | C# worker/tasks | Unload sker med noll aktiva workers och ensures returnerar. **Loggstödd mot-evidens.** | Behåll cancellation/drain/generation publication; prioritera inte större indexrefactor för denna crash. |

### Det mest intressanta riskmönstret i `AutocompleteEditorBinding`

`AutocompleteEditorBinding` gör i dag flera bra kontroller: den verifierar ScriptEditor, current script, current editor, `GetBaseEditor()`, `CodeEdit`-native identity, current host/generation/transition och därefter binding lease. fileciteturn25file0L1-L2

Det saknas däremot ett begrepp motsvarande:

```text
ReloadStabilizationEpoch
eller
NativeEditorReadyGeneration
```

Så en binding kan semantiskt vara:

```text
ManagedAssemblyGeneration = new
ScriptTransitionId        = current
CodeEditInstanceId        = current
path                       = current
```

och ändå ligga i den första perioden efter en native/managed handoff där editorstate nyligen varit föremål för assembly reload, script revalidation, wrapper rebinding, reconnect och deferred UI/text work.

Detta är enligt min bedömning **den viktigaste ownership-dimension som saknas**.

### Native CodeEdit ownership bridge: rätt idé, fel sak att använda som readiness-signal

Bridgen som lämnar en generation-tagged ownership marker på native CodeEdit är en bra design. Den gör det möjligt för generation B att veta att generation A ändrade prefix/theme state och att återställa det utan att lita på generation A:s managed objekt. fileciteturn38file0

Men den bör betraktas som:

> **cleanup/adoption ledger**

inte som:

> **bevis att CodeEdit är redo för ny autocomplete-authority**.

Crashloggen är ett perfekt exempel: orphan recovery lyckas och `EnsureLifecycleCurrent` returnerar. fileciteturn8file3 Det visar att cleanupen fungerar; det bevisar inte att resten av native CodeEdit/ScriptEditor-state hade någon definierad post-reload quiescence guarantee.

### Completion-state-gapet

Nuvarande native ownership marker verkar huvudsakligen följa System Explorer-owned presentation state såsom completion prefixes och `completion_existing_color`. Den representerar däremot inte hela Godots native `CodeEdit` completion-session-state — aktiv popup, option list, selected index, internal prefix/caret relationship med mera. `CodeEdit::confirm_code_completion()` visar uttryckligen att detta är ett aktivt state machine-tillstånd och inte bara en statisk lista. fileciteturn40file0

Det gör följande mekanism plausibel:

```text
generation A har native completion/text state
    ↓
managed graph för A försvinner/reset:as
    ↓
CodeEdit överlever
    ↓
generation B återansluter
    ↓
managed completion state betraktar sig som cold/current
men
native CodeEdit har en historik/state som inte representeras av managed reset
```

Detta är **Spekulativ hypotes / Source-stödd risk**, inte en verifierad defekt. Motargumentet är starkt: System Explorer klarar många completion-/TextChanged-/automatic-using-operationer både utan reload och efter reload. Därför är detta mer sannolikt en möjlig del av reload-riskområdet än en ensam root cause.

### Försvagade hypoteser, sammanfattat

Följande bör enligt nuvarande evidens **inte** styra nästa utvecklingsrunda:

- **“Lägg fler boundaries inne i `EnsureLifecycleCurrent()`.”** Den progressionen är redan visad returnera normalt genom crashsessionen. fileciteturn8file0
- **“Project index kraschar under unload.”** Noll aktiva workers vid unload är betydande negativ evidens.
- **“`EditScript` kraschar där och då.”** Calls returnerar regelbundet även post-reload. fileciteturn8file1
- **“Navigation Stress är defekten.”** No-crash kontrollen uthärdar betydligt fler navigationsoperationer. fileciteturn9file0
- **“Automatic using eller TextChanged är ensam orsak.”** De körs framgångsrikt i den negativa kontrollen och post-reload.
- **“Sista loggade `TextChanged`-callback är crash-stack.”** Godots text/editor internals har deferred state; den sista managed callbacken returnerar och loggen avslutas därefter abrupt. fileciteturn41file1

Det betyder inte att dessa funktioner är irrelevanta. De är **mutationskällor som kan exploatera ett reload-skadat eller otillräckligt stabiliserat state**, men den distinktionen är central.

## Upstream Godot-fynd och bedömning av nuvarande arkitektur

Jag hittade ingen Godot-fix i senare 4.6.x/4.7-material som direkt kan sägas vara “fixen för System Explorers ScriptEditor/CodeEdit hard crash efter C# build”. Det vore därför fel att rekommendera en versionsuppgradering som verifierad lösning. De relevanta upstream-fynden visar i stället att samma **lifecycleklass** — C# Tool objects, signals och editor reload — har varit problematisk över flera Godot 4-versioner.

**Godot #102455 — “Building C# solution leads to errors if signals are connected in the editor”.** Testad i 4.3, fortfarande markerad confirmed/open i hämtad metadata. Efter rebuild kan signalstate ändras så att senare disconnect ger nonexistent-connection-fel, och upprepad rebuild kan leda till disposed-object/failure-to-unload-problem. fileciteturn58file0L3-L44 **Upstream-stödd relevans:** visar att editor-surviving native/resource signalstate och managed reload har verkliga edge cases. **Inte verifierad koppling:** annan signaltyp, äldre version, inget ScriptEditor/CodeEdit hard-crash-bevis.

**Godot #84394 — “[Tool] Changed event ... does not reset properly on Build”.** Den rapporterar repeated event subscription efter builds i 4.1/4.2. fileciteturn59file0L3-L35 **Relevans:** stöd för att repeated managed generations kan lämna oväntad signal-lifecycle. **Begränsning:** gammal version och Resource `changed`, inte System Explorer.

**Godot #78513 — .NET assembly unload tracker.** Godot beskriver att assembly unloading kan misslyckas av flera skäl och rekommenderar explicit cleanup av sådant som håller den collectible assemblyn levande. Ärendet är fortfarande ett aktivt trackerärende och var uppdaterat i augusti 2026. fileciteturn60file0L3-L40 **Relevans:** assembly unload är en verklig separat lifecycle-domän. **Mot System Explorer root-cause:** crashloggen visar inte det typiska “failed to unload assemblies”-felet och project-index workers är noll, så det finns inget starkt stöd för att System Explorers ALC helt enkelt misslyckas att unload:a.

**Godot #87147 — C# tool script can hard-crash editor on build under re-instantiation failure.** I det fallet orsakar en tool class utan parameterless constructor editor crash vid build/reload. fileciteturn63file0L3-L45 Det visar att “build returnerade / editorreload” verkligen kan leda till hard native/editor failures i Tool lifecycle. Mekanismen matchar dock inte System Explorer-loggen och bör inte importeras som förklaring.

Senare 4.6/4.7-sökning gav även andra editorcrashes, men inga som source-mässigt binder ihop exakt `editor_script_changed`, CodeEdit completion och C# ALC reload på ett sätt som förklarar denna A/B. Jag betraktar därför upstream som **stöd för problemklassen**, inte som substitut för den lokala sourceanalysen. Exempelvis finns en separat 4.7-dev tool-script/editor crash som uttryckligen inte reproducerades i rapportörens 4.6-miljö; den är för ospecifik för att vara stark evidens här. citeturn7search3

### Bedömning av System Explorers nuvarande lifecycle primitives

**`ManagedAssemblyGeneration` — behåll. Mycket värdefull.** Den löser en verklig ownership-dimension: kod från generation A ska inte få auktoritet i B. Den används konsekvent för plugin-owned deferred work och recovery. PartialMap verifierar att managed generation är central i reload-arkitekturen. fileciteturn66file0L1-L2

**`HostInstanceToken` — behåll.** Den skiljer inte bara assemblies utan olika autocomplete-hostar inom samma process/generation. Att token flyttas när hosten pensioneras gör stale callback rejection betydligt starkare.

**`ScriptTransitionId` — behåll.** Den modellerar rätt sak: vilken script transition som just nu har semantic authority. Same-target-idempotence förhindrar onödig churn när samma script signaleras flera gånger. fileciteturn15file0L1-L7

**`BindingEpoch` — behåll men gör dess precondition starkare.** En epoch är en bra lease-version när en specifik ScriptEditorBase/CodeEdit-binding har accepterats. Problemet är inte epoch-begreppet; problemet är att “binding kan commit:as” i dag huvudsakligen avgörs av generation + transition + current native identity/path. Det bör dessutom kräva att post-reload stabilization authority är öppen.

**`ScriptEditorLifecycleCoordinator` — bra arkitektonisk kärna.** Den har redan rätt modell för transition, pending binding, stable lease och invalidation. Jag skulle inte ersätta den med en enda enorm coordinator. Script transition authority och native mutation authority är två olika concerns.

**Native ownership bridge — behåll, men begränsa dess semantik.** Den löser det verkliga problemet att presentation-state ligger kvar på ett native objekt efter managed generation A. Crashloggen verifierar dess användningsfall. fileciteturn8file3 Den ska däremot inte vara del av readiness-beviset.

**Deferred guards — bra för plugin-owned deferred work, otillräckliga som global lifecycle-lösning.** Godots egna deferred callbacks saknar System Explorers tokens. fileciteturn41file1

### Roadmapen: rätt riktning, men fel att börja med enbart serialization

Den aktuella `LifecycleRoadmap`-filen innehåller ett internt source-of-truth-SHA från en äldre analys och kan därför inte användas som bevis för dagens `main`; dagens faktiska `main` är `0c198e3…`. Roadmapen är ändå värdefull strategisk input. fileciteturn67file0L1-L2

Idén om en central CodeEdit mutation/transaction coordinator är sund. Den bör på sikt äga åtminstone:

```text
ConfirmCodeCompletion
CancelCodeCompletion
InsertText / plugin-owned text mutation
completion prefix mutation
completion theme override mutation
eventual completion-session reset/adoption policy
```

och sannolikt lifecycle activation/deactivation för signalerna som ger pluginet rätt att reagera på CodeEdit.

Men **serialization räcker inte**. Föreställ följande:

```text
native editor är ännu inte reload-stabil
    ↓
mutation coordinator tar exklusivt lock
    ↓
den utför exakt en välserialiserad mutation
```

Det är fortfarande en mutation vid fel lifecycle-tidpunkt.

Därför bör ordningen vara:

```text
reload quiescence / readiness authority
        ↓
stable ScriptEditor binding lease
        ↓
CodeEdit mutation coordinator
```

inte tvärtom.

Namespace Refactor-quiescence i dagens arkitektur illustrerar redan den generella principen: när ett subsystem gör en riskabel multi-step mutation stoppas autocomplete-arbete och återupptas först senare. PartialMap beskriver detta uttryckligen som en begränsad, feature-owned quiescence och inte den generella slutarkitekturen. fileciteturn66file0L1-L2 Reload behöver samma idé, men som **global lifecycle authority**, inte som ännu en lokal feature-flagga.

## Stable-after-rebuild-arkitektur och rekommenderad nästa åtgärd

### Primär rekommendation

**Inför en explicit managed-reload quiescence/stabilization barrier som nästa correctness-refactor.**

Inte mer generell crash-tail-instrumentering först. Inte hela roadmap-coordinatorn först. Inte en bred rewrite av project index.

Anledningen är att source + A/B redan svarar på den viktigaste arkitekturfrågan: det finns en lifecycle-dimension vid rebuild som nuvarande tokens inte representerar, och crashloggen bevisar att ny managed autocomplete faktiskt återansluter till native CodeEdit-state från den föregående generationen. fileciteturn8file3

Barriären ska inte vara en godtycklig `await 500 ms`, timer eller “vänta två sekunder efter build”. Den bör vara **state-baserad och main-thread-baserad**.

### Föreslagna invariants

**När ALC unload/recovery börjar ska autocomplete gå till ett explicit `ReloadQuiescent`-läge.** Från den punkten får ingen CodeEdit-mutation auktoriseras. Gamla binding leases ska anses förbrukade oavsett om native instance IDs fortfarande finns.

Under `ReloadQuiescent` bör följande vara förbjudet:

```text
ConfirmCodeCompletion
CancelCodeCompletion
InsertText
completion request/confirmation processing
prefix/theme mutation
binding commit till Stable
automatic-using execution
TextChanged-driven completion mutation
andra autocomplete-native mutationer
```

Callbacks får eventuellt observera state för att bygga readiness, men de ska inte skapa ny feature-side effect.

**Alla gamla plugin-owned deferred operations ska dö över generationgränsen.** Det mesta av denna invariant finns redan genom `ManagedAssemblyGeneration`, host tokens och operation tokens och bör bevaras. fileciteturn18file0L1-L2

**Native objekt får överleva; deras gamla managed lease får inte göra det.** Samma CodeEdit ID får alltså återanvändas, men bara som en ny candidate binding. `CodeEditNativeInstanceId == gammalt ID` ska varken vara fel eller readiness-bevis.

**Native ownership reconciliation måste ske före activation, men reconciliation öppnar inte barriären.** `AutocompleteCodeEditNativeOwnershipBridge` får återställa gammal prefix/theme ownership, men resultatet ska fortfarande vara “candidate CodeEdit, not authorized CodeEdit”.

**Ny managed generation får först aktivera autocomplete när en coherent editor tuple har observerats stabilt:**

```text
ManagedRecoveryInProgress == false

ScriptEditor är valid
current script är den förväntade/current
current ScriptEditorBase är valid
BaseEditor är CodeEdit
native IDs är coherent
script path matchar
ingen ny ScriptTransitionId har börjat
ingen host/generation transition har skett
```

och, som **Arkitekturell inferens**, bör samma tuple därefter fortfarande vara identisk efter minst **en Godot main-thread deferred/message-queue boundary**.

Det sista kravet är inte ett dokumenterat Godot-API-kontrakt. Jag rekommenderar det därför inte som “Godot säger att en frame räcker”, utan som ett explicit System Explorer-stability criterion mot en source-verifierad verklighet: ScriptEditor/TextEdit själva använder deferred ordering. fileciteturn24file1 fileciteturn41file1

En bättre modell är alltså:

```text
generation B starts
    ↓
ReloadQuiescent

observe tuple T
    ↓
defer exactly one main-thread stabilization check
    ↓
observe tuple T again

T unchanged?
    ├─ no  → restart observation
    └─ yes → issue ReloadReadyEpoch R
              ↓
              allow new BindingEpoch
              ↓
              connect/activate autocomplete mutation processing
```

Snabba extra `editor_script_changed` eller CodeEdit replacement invalidaterar candidate readiness och startar om observationen. Det är state-driven, inte timeout-driven.

### Separera authorities

Jag rekommenderar tre uttryckliga authorities:

```text
Managed reload authority
    "får denna managed generation alls använda editorintegration nu?"

ScriptEditor lifecycle authority
    "vilket script/editor/CodeEdit är current?"

CodeEdit mutation authority
    "vem får mutera den aktuella CodeEdit och i vilken transaction?"
```

`ScriptEditorLifecycleCoordinator` bör fortsätta äga den mittersta frågan. Den framtida coordinatorn bör äga den sista. Reload-barriären bör äga den första.

Att stoppa in alla tre i samma class riskerar en ny monolit och gör `ScriptTransitionId` semantiskt otydligt. Ett scriptbyte är inte samma event som en assembly reload, och en textmutation är inte samma event som en ScriptEditor transition.

### Konkreta implementation directions utan patch

**`SystemExplorerPlugin.EditorReloadLifecycle.cs`** bör äga start/slut på reload-quiescence eftersom den redan äger `ManagedAssemblyGeneration`, recovery och integration reconstruction. Den ska inte börja äga CodeEdit-mutationsdetaljer. PartialMap placerar redan reloadansvaret där. fileciteturn66file0L1-L2

**`ScriptEditorLifecycleCoordinator.cs`** bör behålla sitt nuvarande ansvar för transition/lease/currentness. En commit till en “usable stable binding” bör däremot kräva en extern/current reload-ready epoch, eller så bör dess stable lease konsumeras av en separat activation authority som inte öppnar feature processing förrän reload readiness verifierats. fileciteturn15file0L1-L7

**`SystemExplorerPlugin.Autocomplete.cs`** bör göra generation/host/transition-guards till nödvändiga men inte tillräckliga villkor. Deferred rebind kan fortfarande användas för observation under recovery, men feature processing/mutation ska kräva reload-ready authority. fileciteturn18file0L1-L2

**`AutocompleteEditorBinding.cs`** bör separera “resolve candidate tuple” från “activate binding”. I dag sker query, signal ownership, native ownership recovery och binding authority relativt tätt. En robust reloadmodell vinner på att kunna läsa:

```text
candidate ScriptEditor / ScriptEditorBase / CodeEdit
```

utan att omedelbart ge candidate CodeEdit full autocomplete-authority. fileciteturn25file0L1-L2

**`AutocompleteCodeEditNativeOwnershipBridge.cs`** bör förbli recovery/cleanup ledger. Den ska inte växa till den globala coordinatorn.

**`AutocompletePluginHost.cs`** bör ha ett explicit suspended/quiescent feature-state så att “host exists” inte innebär “native autocomplete processing är aktivt”. Dess `EnsureLifecycleCurrent`-progression ska fortfarande kunna returnera normalt utan att det i sig tolkas som att reload barrier är öppen. Detta är särskilt viktigt eftersom loggen redan visar att ensure returnerar normalt i crashsessionen. fileciteturn8file0

**`AutocompleteProjectTypeConfirmationService` och automatic-using execution** bör, när den senare mutation coordinatorn införs, konsumera en immutable current binding/reload lease och låta coordinatorn vara ensam authority för `ConfirmCodeCompletion`/`InsertText`. fileciteturn33file0L1-L2

Det som ska förbli funktionellt oförändrat i denna första correctness-refactor är vanlig navigation, project index, automatic-using-semantik när lifecycle är stabil, tree filtering, Namespace Refactor-funktionalitet och Navigation Stress-harnessens normala navigation path. Refactorn ska inte samtidigt återaktivera semantic pipeline, overlay-funktioner eller andra diagnostiskt avstängda autocomplete-features.

### Varför jag inte rekommenderar mer bred logging nu

Sourceanalysen besvarar redan flera frågor som ytterligare per-navigation-loggning annars skulle försöka besvara:

- samma native CodeEdit överlever rebuild,
- ny managed host återtar den,
- `EditScript` returnerar,
- full ensure-progression returnerar,
- project-index workers är noll vid unload,
- System Explorers gamla deferred work är redan kraftigt generation/token-guardat,
- Godot har själv deferred ScriptEditor/TextEdit-state.

Den viktigaste obesvarade frågan är därför inte “vilken av ytterligare 20 managed methods var sist?”, utan “försvinner crashklassen om vi förbjuder native autocomplete authority tills den nya generationen har passerat en explicit stabilization boundary?”

Det testet görs bäst som en **correctness-refactor med en klar invariant**, inte genom mer högvolymsinstrumentering.

Om barriären trots detta inte förändrar reproducerbarheten skulle nästa diagnostiska steg kunna vara mycket smalare: endast ett reload-tail-stateprov för native completion-session-state, inte ytterligare per-navigation trace.

### De starkaste kvarvarande kandidaterna

Eftersom det saknas en native crash-stack går det inte att fastställa exakt instruktion. Tre kandidater återstår med tydligt olika styrka.

**Starkast: för tidigt återtagande av överlevande native ScriptEditor/CodeEdit efter managed generation transition.** Mekanismen är att generation A:s managed graph försvinner medan CodeEdit överlever; generation B rekonstruerar host/signals/wrapperrelationer och commit:ar en ny binding utifrån current identity, men utan explicit native stabilization criterion. **Stöd:** samma native CodeEdit ID verifieras över generationerna; System Explorer har explicit orphan recovery; Godot har deferred editor/text lifecycle; `IsInstanceValid` är bara pointer-validity. fileciteturn8file3 fileciteturn62file0L1-L7 **Mot:** den nya generationen klarar mycket fortsatt navigation innan crashen, så mekanismen är sannolikt ett enabling race/state-problem, inte en deterministisk first-bind-crash. **Minimal skiljande åtgärd:** den rekommenderade reload stabilization barrier. Om upprepade rebuild-stresstester blir stabila efter den refactorn förändras sannolikheten för denna klass kraftigt.

**Näst starkast: native CodeEdit completion/text state är inte fullständigt generation-neutraliserat.** Managed autocomplete-state återställs, men CodeEdit har egen completion session/caret/text state och normal rebind använder för närvarande inte native completion cancellation. `ConfirmCodeCompletion` är en native complex edit och TextChanged är deferred. fileciteturn40file7 fileciteturn41file1 **Stöd:** source; same native CodeEdit survives. **Mot:** samma funktioner fungerar tusentals gånger i no-crash-generationen och många gånger post-reload. **Minimal skiljande åtgärd om barriären inte räcker:** ett reload-specifikt, coordinator-owned test där native completion-state neutraliseras exakt en gång vid ny binding activation, inte generell extra logging.

**Tredje: Godot-internal deferred ScriptEditor/TextEdit-arbete korsar generation/transition och möter den nya hostens signaler eller mutationer.** **Stöd:** ScriptEditor och TextEdit har source-verifierade deferred paths; System Explorers generation tokens kan inte guard:a Godots interna MessageQueue-intent. fileciteturn24file1 fileciteturn41file1 **Mot:** ingen queue provenance eller native stack visar att just ett pre-reload callback överlever i crashfallet. **Minimal skiljande åtgärd om de två första inte löser det:** en separat minimal Godot-upstream-reproduktion med C# tool/editor plugin, surviving ScriptEditor/CodeEdit, rebuild och repetitiv script switching/completion, hellre än fler boundaries i hela System Explorer.

### Slutlig bedömning av den extra viktiga frågan

**Vad förändras konkret vid rebuild på ett sätt som bäst förklarar att autocomplete blir mindre stabilt?**

Inte navigationen. Inte nödvändigtvis `EditScript`. Inte antalet lifecycle ensures.

Det som förändras är **ownership-generationen på den managed sidan utan motsvarande total reset av native editor-state**:

```text
före rebuild
─────────────
managed host A
managed signal/callback graph A
managed binding lease A
ManagedAssemblyGeneration A
        ↕
native ScriptEditor
native ScriptEditorBase
native CodeEdit X
native text/completion/theme/editor history

assembly rebuild
──────────────
ALC A unloads
managed host A försvinner
managed lease A invalideras

men Godot Editor-processen fortsätter
och CodeEdit X kan fortsätta existera

efter rebuild
─────────────
ManagedAssemblyGeneration B
managed host B
nya managed callbacks/wrappers/leases
        ↕
samma eller återanvänd historisk native editor state,
inklusive verifierat samma CodeEdit X i crashloggen
```

fileciteturn8file3

**Det är den asymmetrin som inte finns i no-rebuild-kontrollen.**

System Explorer har redan starka lösningar för *stale managed generation*. Det som saknas är motsvarande regel för *surviving native editor state*: ett native objekt får vara levande och identity-current utan att ännu vara auktoriserat för ny managed autocomplete-mutation.

Därför är nästa utvecklingssteg enligt evidensen **en permanent reload correctness-refactor med explicit quiescence/stabilization barrier**. Den planerade mutation coordinatorn bör byggas ovanpå den barriären därefter. Detta adresserar den starkaste experimentvariabeln direkt, förbättrar säkerheten vid upprepade rebuilds i samma Godot-process och undviker att låsa utvecklingen i ytterligare en serie `diagnostic boundary → crash → två nya boundaries`.