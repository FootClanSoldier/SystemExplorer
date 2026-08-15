# System Explorer Autocomplete Lifecycle Stability Roadmap för Godot 4.6.3 .NET/C#

## Executive summary och verifierad källbas

Den här analysen använder två fasta implementationer som source of truth:

| Komponent | Verifierad version | Source of truth |
|---|---|---|
| **System Explorer `main`** | `30a6b6cf2fd18b9c3e8b8472f42d4911004fef3a` | Exakt `main`-commit som resolve:ades vid analysen. fileciteturn0file0L1-L13 |
| **Godot 4.6.3 stable** | `35e80b3a8822a9df9be390814b62f44c0a9c69e8` | Exakt commit bakom taggen `4.6.3-stable`. fileciteturn5file0L1-L13 |

Godot 4.6.3 stable publicerades i maj 2026; analysen nedan utgår konsekvent från committen ovan, inte från `master` eller senare 4.7-kod. citeturn2search0

I System Explorer-committen finns de begärda källorna: `addons/system_explorer/Scripts`, autocomplete-implementationen inklusive `SystemExplorerPlugin.Autocomplete.cs`, `addons/system_explorer/Scripts/docs/SystemExplorerPlugin_PartialMap.txt` och `logs/CrashLog_11.log`. PartialMap beskriver uttryckligen den reload-safe autocomplete-komposition och Namespace Refactor-quiescence som den faktiska koden också visar; där dokument och kod skulle skilja sig har koden behandlats som auktoritativ. fileciteturn56file0L1-L2

De viktigaste slutsatserna är:

| Slutsats | Evidens |
|---|---|
| **Den permanenta lösningen bör inte vara fler oberoende bool/barrier-patchar. System Explorer behöver en enda explicit main-thread lifecycle/mutation coordinator för ScriptEditor/CodeEdit.** Dagens bools och tokens representerar redan en implicit state machine. | **Source-verifierad + arkitekturell inferens.** Autocomplete, ScriptEditor-rebind, TextChanged-validation, automatic using, Namespace Refactor och reload har varsin lokal barrier men delar samma native editorobjekt. fileciteturn6file0L1-L7 |
| **Deferred `editor_script_changed`-rebind bör permanentas.** Godot 4.6.3 emitterar `editor_script_changed` medan ScriptEditors egen transition fortfarande kan ha arbete kvar; i faktiska ScriptEditor-flöden följs signalen av fortsatt `ScriptEditorBase::validate()`. Att göra full CodeEdit-rebind inuti den externa signalstacken skapar alltså en verklig reentrancy-risk. | **Source-verifierad.** fileciteturn16file0 |
| **Den nuvarande deferred automatic-using-modellen är principiellt rätt:** en enda `ConfirmCodeCompletion`, `AcceptEvent`, managed `GuiInput` returnerar och den sekundära `InsertText` görs först deferred. Dessutom visar Godot att `gui_input`-signalen avsiktligt emitteras *före* Controls egen `_gui_input`, just för att en extern handler ska kunna acceptera/override:a eventet. | **Source-verifierad.** fileciteturn59file0 fileciteturn65file0L2-L6 |
| **CrashLog_11 pekar inte ut completion-confirm/cancel som sista aktiva operation.** Den sista loggade fasen ligger i Namespace Refactors öppna ScriptEditor-bufferinventering medan autocomplete-quiescence är aktiv. Detta är stark korrelation men inte bevis på native root cause. | **Loggstödd.** fileciteturn11file0L35660-L35742 fileciteturn54file0L1-L7 |
| **“Stale ManagedCallable från autocomplete-signaler” är betydligt svagare som huvudteori än det först låter.** System Explorers reload-safe signalhelpers använder `new Callable(this, methodName)`, alltså target-object + metodnamn, inte delegate-baserad `ManagedCallable`. Godot har dessutom separat hot-reload-serialisering för riktiga managed delegates. | **Source-verifierad motbevisning.** fileciteturn47file0L1-L7 fileciteturn52file0L1-L7 fileciteturn38file0L1-L7 |
| **Named `CallDeferred` löser stale-trampoline-problemet men inte stale-intent-problemet.** Godot köar ObjectID + method name + Variant-argument och slår upp target vid execution. En call som schemalagts före reload kan därför i princip nå det överlevande native pluginobjektets *nya* managed generation. Alla editorrelaterade deferred operations bör därför bära explicit assembly generation + host token + binding epoch. | **Source-verifierad + arkitekturell inferens.** fileciteturn50file0L1-L2 |
| **Bakgrundsindexeringen är inte den främsta CrashLog_11-misstanken.** `AssemblyLoadContext.Unloading` stänger worker-lifetime, cancellation utfärdas, publication är latest-wins/lifetime-guardad, och sista reloaden i loggen visar noll aktiva workers. Gamla Tasks kan hålla en gammal ALC levande tills de faktiskt avslutas, men det är i första hand ett managed unload/liveness-problem så länge de aldrig har GodotObject-referenser eller publicerar tillbaka över generationen. | **Source-verifierad + loggstödd.** fileciteturn29file0 fileciteturn30file0 fileciteturn11file0L35660-L35742 |

**Huvudrekommendationen är därför:** bygg inte slutarkitekturen runt “vilken enskild `CancelCodeCompletion()` eller `InsertText()` kraschar?”. Bygg den runt **explicit ownership av när någon över huvud taget får röra ScriptEditor/CodeEdit**. En managed generation ska äga en kall autocomplete-host; en separat binding-epoch ska identifiera exakt ScriptEditor/CodeEdit; all native mutation ska serialiseras via en coordinator; deferred work ska vara generation- och binding-bound; workers ska vara helt Godot-fria. Först därefter återaktiveras semantic/overlay/richer completion.

## Verified current architecture och native boundary call graph

Den förväntade diagnostiska composition mode-konfigurationen finns faktiskt i den analyserade committen i `addons/system_explorer/Scripts/Partials/SystemExplorerPlugin.Autocomplete.cs`, nära filens konfigurationsfält:

```text
semanticMemberPipelineEnabled                         = false
cancelNativeCompletionOnRebind                       = false
activeDocumentSyntaxOverlayEnabled                    = false
cancelNativeCompletionOnTextChangedValidation         = false
automaticUsingInsertTextExecutionEnabled              = true
automaticUsingDeferInsertTextAfterGuiInputEnabled     = true
automaticUsingComplexOperationWrapperEnabled          = false
```

Det finns samtidigt separata callback-depth-, pending-, execution- och tokenfält för ScriptEditor-change, CodeEdit.GuiInput, deferred automatic using, TextChanged-validation och Namespace Refactor-quiescence. Detta verifierar att `main` verkligen är en diagnostiskt reducerad modell och inte bör läsas som den avsedda feature-slutdesignen. fileciteturn6file0L1-L7

**System Explorer autocomplete-callgraphen ser i praktiken ut så här i commit `30a6…`:**

```text
ScriptEditor.editor_script_changed
    ↓
OnAutocompleteScriptChanged
    ├─ callback depth++
    ├─ EnsureManagedAssemblyStateCurrent()
    ├─ invalidate managed validation / mark pending
    ├─ latest-wins QueueDeferredAutocompleteScriptChangeRebind()
    └─ callback returns
           ↓
       named CallDeferred
           ↓
ApplyDeferredAutocompleteScriptChangeRebind(token)
    ├─ validate plugin/lifecycle/token/quiescence
    ├─ EnsureManagedAssemblyStateCurrent()
    ├─ host.EnsureLifecycleCurrent(refreshCodeEditBinding:false)
    └─ host.HandleScriptChanged(...)
           ↓
       resolve/rebind current CodeEdit
```

Det här flödet finns i `SystemExplorerPlugin.Autocomplete.cs`, främst området med `QueueDeferredAutocompleteScriptChangeRebind`, `OnAutocompleteScriptChanged` och `ApplyDeferredAutocompleteScriptChangeRebind`. Den deferred callbacken är latest-wins/coalesced, och den synkrona signalcallbacken gör inte längre full CodeEdit-resolution/rebind. fileciteturn19file0 fileciteturn22file0

Den aktiva project-type confirmation-vägen är:

```text
CodeEdit.gui_input signal
    ↓
OnAutocompleteCodeEditGuiInput
    ↓
AutocompletePluginHost.HandleCodeEditGuiInput
    ↓
AutocompleteCompletionConfirmationBridge
    ├─ GetCodeCompletionSelectedIndex()
    ├─ GetCodeCompletionOption(...)
    ├─ verifiera System Explorer-owned project-type option
    ↓
AutocompleteProjectTypeConfirmationService
    ├─ planera automatic using
    └─ exakt en ConfirmCodeCompletion(replace)
    ↓
CodeEdit.AcceptEvent()
    ↓
managed GuiInput callback returnerar
    ↓
QueueDeferredAutocompleteUsingInsertion(...)
    ↓
named CallDeferred(token)
    ↓
ApplyDeferredAutocompleteUsingInsertion
    ├─ generation == ManagedAssemblyGeneration
    ├─ host token == current HostInstanceToken
    ├─ GuiInput depth == 0
    ├─ ingen ScriptEditor barrier
    ├─ ingen Namespace quiescence
    ├─ verifiera current CodeEdit/script
    └─ exakt en CodeEdit.InsertText(using...)
```

Detta är exakt den modell du beskrev. Den deferred grenen i `AutocompleteProjectTypeConfirmationService` gör inte en synkron `ConfirmCodeCompletion -> InsertText`; den returnerar en insertion plan efter confirmation. Själva deferred operationen revaliderar generation, host och editorbindning innan `InsertText`. fileciteturn27file0 fileciteturn28file0 fileciteturn20file0

Det finns ytterligare ett viktigt lager: `OnAutocompleteCodeEditGuiInput` håller `_autocompleteCodeEditGuiInputCallbackDepth`, och automatic-using execution kräver att djupet åter är noll. Det är därför inte bara “CallDeferred för bekvämlighet”, utan en explicit stack barrier. fileciteturn20file0 fileciteturn23file0

**TextChanged-vägen** är också tvåstegad:

```text
CodeEdit.TextChanged
    ↓
OnAutocompleteCodeEditTextChanged
    ├─ suppress/mark pending om using/rebind/quiescence är aktiv
    ├─ host.BeginTextChangedValidation()
    └─ CallDeferred(
           ValidateAutocompleteAfterTextChangedDeferred,
           hostInstanceToken,
           validationGeneration)
             ↓
         revalidate lifecycle + token + generation
             ↓
         host.ValidateAfterTextChanged(...)
```

Native `CancelCodeCompletion()` från själva deferred-validationen är diagnostiskt isolerad med `cancelNativeCompletionOnTextChangedValidation:false`; de managed session/state transitions som avgör om completion blivit dormant/stale finns däremot kvar. fileciteturn23file0 fileciteturn26file0

**Binding-lagret** finns huvudsakligen i `AutocompleteEditorBinding`. Det hämtar `ScriptEditor`, läser `GetCurrentScript()`, `GetCurrentEditor()`, `ScriptEditorBase.GetBaseEditor()`, kräver C# och en `CodeEdit`, ansluter `TextChanged`, `CodeCompletionRequested` och `GuiInput`, och lägger på System Explorers prefixes/theme-state. `TryGetActiveCodeEdit()` gör därefter en ny current-script/current-editor/base-editor-kontroll och kräver samma native instance ID. fileciteturn24file0

Vid normal rebind anropas `DisconnectCodeEdit(cancelCompletion: _cancelNativeCompletionOnRebind)`, vilket med den nuvarande flaggan betyder **ingen** native cancellation. Vid full shutdown används däremot `cancelCompletion:true`. Disconnect-sekvensen invalidaterar först managed completion state, kopplar bort signaler, gör eventuell native cancellation, återställer prefix/theme och släpper marker/ägande. fileciteturn25file0L1-L7

**Prefix/theme/native ownership** är mer genomtänkt än en vanlig managed snapshot. `AutocompleteCodeEditNativeOwnershipBridge` skriver metadata direkt på den överlevande native CodeEdit-instansen under nyckeln `_system_explorer_autocomplete_code_edit_state_v1`. Markern lagrar bland annat schema, owner assembly generation, CodeEdit instance ID, tidigare completion-prefixes och tidigare `completion_existing_color`-override. En ny managed generation kan därmed identifiera och återställa “orphaned” state från föregående generation. fileciteturn57file0L1-L2

Detta är **en bra permanent princip**, med en viktig begränsning: metadata är en ownership ledger, inte ett lås. `SetMeta`, prefixändringar och theme overrides är fortfarande native mutationer. De måste i slutarkitekturen gå genom samma binding/mutation coordinator som övriga CodeEdit-operationer. `BeginBulkThemeOverride/EndBulkThemeOverride` kan minska theme-notification-churn men skapar inte i sig någon managed-reload- eller ScriptEditor-lifecycle-barriär.

**Managed assembly recovery** är redan relativt stark. `ManagedAssemblyGeneration` är en per-assembly GUID. Recovery reset:ar transient state, försöker återansluta överlevande UI och faller tillbaka till full integration rebuild. En autocomplete-host vars generation inte matchar current assembly generation pensioneras; ny host cold-composes. Vid shutdown detach:as `_autocompleteHost` och host token flyttas **före** `Shutdown()` körs på det detached objektet. Det minskar risken att callbacks under shutdown hittar en host som håller på att dö. fileciteturn46file0L1-L2 fileciteturn22file0

Det bör behållas.

Det jag däremot skulle ändra permanent är att göra generation-boundary ännu hårdare: **ingen autocomplete feature graph bör betraktas som reload-surviving state.** Överlevande native editorobjekt får återanvändas; managed host, coordinatorer, Roslyn-caches och feature services ska betraktas som generation-owned och kallstartas.

**Namespace Refactor-quiescence** är också verifierad. Operationen aktiverar quiescence före den egentliga editor-/filmutationen. Autocomplete-callbacks som kommer under quiescence exekverar inte sitt normala arbete utan sätter pending-flaggor. Vid release konsolideras script/text-behov till högst en `HandleScriptChanged`-resync och filesystem-pending till en project-index refresh. PartialMap beskriver uttryckligen detta som en diagnostisk A/B-isolering, inte som en generell mutation scope. fileciteturn56file0L1-L2 fileciteturn20file0 fileciteturn21file0

Min bedömning är att just den **konkreta** Namespace Refactor-implementeringen är specialfall, men idén den visar är generell: System Explorer behöver en gemensam modell för “någon annan äger just nu editor mutation”.

## CrashLog_11 – rekonstruktion och vad loggen faktiskt bevisar

CrashLog_11 visar en lång editorprocess med upprepade managed assembly generations, medan viktiga native Godot-identiteter fortsätter att existera. Detta är precis den miljö där en lifecycle-design måste skilja native object lifetime från managed generation lifetime. fileciteturn11file0L35660-L35742

Den sista reloadcykeln kan rekonstrueras ungefär så här:

| Tid | Observation | Tolkning |
|---|---|---|
| `20:40:47.742` | Gammal `AssemblyLoadContext` går in i `Unloading`. AutocompleteIndexLifetime rapporterar cancellation; aktiva workers är noll och drain slutförs. | **Loggstödd:** inga project-index workers ser ut att vara kvar i just denna reload-tail. |
| `20:40:48.982` | Ny `ManagedAssemblyGeneration = 84f1900d…`; transient autocomplete reset med host null och host-token kring nästa generation. | Managed graph rekonstrueras efter reload. |
| `20:40:49.007` | Ny autocomplete host cold-composes, host instance token 10. | Bekräftar att host-generationer byts medan editorn fortsätter leva. |
| `20:40:49.011–.013` | CodeEdit-native marker från den föregående generationen hittas som orphan och återställs på en överlevande CodeEdit. | Mycket viktig **source + log validation** av native ownership-marker-idén. |
| `20:40:49.020` | CodeEdit rebind/index initialization slutförs. | Recovery ser normal ut. |
| `20:40:50.418` | System Explorer påbörjar `EditScript` för `PlayerInput.cs`. | ScriptEditor-transition börjar. |
| `20:40:50.420–.421` | `editor_script_changed` når System Explorer på callback-depth 1; deferred rebind token 88 köas. | Den nya stack-unwind-modellen används. |
| `20:40:50.423` | `EditScript` returnerar. | Synkrona script-change-stacken har unwindat. |
| `20:40:50.438–.444` | Deferred ScriptEditor rebind körs: lifecycle ensure utan refresh, därefter `HandleScriptChanged`, ny CodeEdit resolve/rebind. | Barriären fungerar enligt design. |
| `20:41:05.342` | Tree selection/navigation. | Normal användning efter reload. |
| `20:41:10.402` | Namespace Refactor bekräftas. | Ny editor-mutating operation börjar. |
| `20:41:10.408` | Autocomplete-quiescence startar, token 26, current host 10/current generation. | Autocomplete ska inte konkurrera under operationen. |
| `20:41:10.474` | Buffer preflight rapporterar 46 öppna scripts. | Namespace Refactor inspekterar ScriptEditor. |
| `20:41:10.481` | **Sista raden:** `Editor controls inspected; GetOpenScriptEditorsCount=46; ValidUniqueTextEditors=46; CurrentScriptPath=...PlayerInput.cs; CurrentEditorIdentified=True; CurrentEditorMatchedDirectly=True`. | Processloggen upphör mitt i buffer locator-flödet. |

Den slutliga raden motsvarar exakt diagnostiken i `ScriptEditorBufferLocator.BuildCompleteOpenEditorGroups`. Direkt efter den loggraden fortsätter koden bland annat genom grupperingen av öppna `TextEdit`-kontroller, `ScriptEditorBufferStateService.IsUnsaved(textEditor)` och senare textverifiering mot editorbuffers. fileciteturn54file0L1-L7

Det ger en viktig korrigering av root-cause-narrativet:

**[Loggstödd] CrashLog_11 slutar inte i `ConfirmCodeCompletion`, `CancelCodeCompletion`, `RequestCodeCompletion`, `InsertText(using)` eller deferred ScriptEditor-rebind.**

**[Loggstödd] Autocomplete-quiescence är redan aktiv när loggen tar slut.**

**[Arkitekturell inferens] Den närmaste observerade managed/native editor boundaryn ligger därför i Namespace Refactors ScriptEditor/TextEdit-bufferinventering.**

**[Inte bevisat] Detta betyder inte att den sista TextEdit-läsningen orsakade kraschen.** Ett native fel kan inträffa asynkront, ett annat Godot-subsystem kan vara aktivt, och en `Begin` utan `Returned` skulle fortfarande bara vara stark temporal korrelation. Här har vi dessutom ingen native crash stack i loggen som binder dödsögonblicket till en specifik C++-funktion.

Detta är ändå värdefullt: CrashLog_11 talar mot en modell där “autocomplete confirm är alltid den direkta kraschen”. Den stödjer i stället ett bredare problemområde: **en långlivad native editor där managed generations byts och flera System Explorer-features samtidigt resolverar, läser eller muterar ScriptEditor/ScriptEditorBase/TextEdit/CodeEdit.**

Det finns också viktig negativ evidens mot worker-teorin. Den sista ALC-unloaden visar ingen aktiv index worker. `AutocompleteIndexLifetime` har dessutom en lifetime shutdown-token, räknar aktiva workers och förhindrar publication genom `TryRunWhileActive` efter shutdown. `CSharpProjectIndexCoordinator` använder latest-request generation och publicerar endast ett resultat som fortfarande är aktuellt. fileciteturn29file0 fileciteturn30file0

Det gör inte gamla Tasks magiskt omöjliga: en `Task.Run` vars delegate kommer från den gamla assemblyn kan fortsätta exekvera managed kod tills cancellation observeras och kan därmed hålla den collectibla ALC:n vid liv längre än önskat. Men **det är normalt ett managed unload/liveness-fel**, inte en native hard-crash-mekanism. Hard-crash-risken uppstår först om sådant gammalt arbete får bära eller återanvända GodotObject/native handles eller publicera tillbaka genom en stale native boundary. Den nuvarande project-index-lifetime-modellen verkar uttryckligen försöka förhindra detta.

## Godot 4.6.3 lifecycle: ScriptEditor, CodeEdit, TextChanged, Callables och CallDeferred

**ScriptEditor-transitionen är ett verkligt riskområde.** I Godot 4.6.3 ligger `ScriptEditor::notify_script_changed()` i `editor/script/script_editor_plugin.cpp` och emitterar `editor_script_changed`. Bland dess callers finns transitioner där sekvensen fortsätter ungefär:

```cpp
seb->ensure_focus();
Ref<Script> scr = seb->get_edited_resource();
if (scr.is_valid()) {
    notify_script_changed(scr);
}
seb->validate();
```

Det betyder att en extern signalcallback körs medan ScriptEditor fortfarande är inne i sin egen transitionstack och kan fortsätta med `ScriptEditorBase::validate()` efter att callbacken returnerat. fileciteturn16file0

Det ger en direkt lifecycle-invariant:

> `editor_script_changed` är en notification point, inte ett bevis på att hela ScriptEditor-transitionen är quiescent.

Att System Explorer tidigare kunde resolvera ny editor, disconnecta gammal CodeEdit, mutera completion state/prefix/theme och eventuellt cancel:a completion direkt från den callbackstacken är därför reentrancy-mässigt tveksamt.

Den aktuella:

```text
signal
→ markera intent
→ returnera
→ deferred
→ resolve current ScriptEditorBase/CodeEdit
```

är inte bara en crash-workaround. **Den bör bli permanent arkitektur.**

### CodeEdit.GuiInput och confirmation

Godots Control-implementation ger ett ovanligt tydligt svar på callback-ordering-frågan. `Control::_call_gui_input()` gör i Godot 4.6.3:

```cpp
emit_signal("gui_input", event); // Signal should be first...
if (!is_inside_tree() || get_viewport()->is_input_handled()) {
    return;
}

GDVIRTUAL_CALL(_gui_input, event);
...
gui_input(event);
```

Kommentaren i koden säger uttryckligen att signalen ska vara först så att den kan override:a ett event och sedan acceptera det. fileciteturn59file0

System Explorers confirmation bridge använder alltså ett **avsiktligt Godot extension point**:

```text
CodeEdit receives event
↓
Control emits gui_input signal first
↓
System Explorer recognizes its own selected completion
↓
ConfirmCodeCompletion(...)
↓
AcceptEvent()
↓
signal returns
↓
Control sees input handled and aborts
↓
CodeEdit::gui_input does not process the same acceptance again
```

Detta är viktigt eftersom `CodeEdit::gui_input` annars, om completion är aktiv och action är `ui_text_completion_accept` eller `ui_text_completion_replace`, själv anropar `confirm_code_completion`, accepterar eventet och returnerar. fileciteturn63file0L1-L2

System Explorer gör alltså inte “confirm och sedan låter Godot confirm:a igen”; `AcceptEvent()` bryter den senare native vägen.

`CodeEdit::confirm_code_completion()` är emellertid en stor muterande transaktion. I 4.6.3 gör den bland annat:

```text
check editable + completion active
begin_complex_operation()
begin_multicaret_edit()

for each relevant caret:
    inspect selected completion
    remove existing completion base / replace range
    insert completion text
    move caret
    handle symbol / brace merging
    possibly remove/insert closing characters

end_multicaret_edit()
end_complex_operation()

cancel_code_completion()

if last completion char is a configured prefix:
    request_code_completion()
```

Den konkreta implementationen finns kring `scene/gui/code_edit.cpp`, området ungefär `request_code_completion` → `add_code_completion_option` → `update_code_completion_options` → `confirm_code_completion` → `cancel_code_completion`. fileciteturn65file0L2-L6

Därför är en `ConfirmCodeCompletion()` i sig redan en full text/caret/completion/undo-transaktion.

**Bedömning:** den enda confirmation som System Explorer gör inuti `gui_input`-signalcallbacken är försvarbar, eftersom Godot uttryckligen erbjuder signal-before-handler för override och System Explorer därefter `AcceptEvent()`-ar. Däremot var den gamla modellen:

```text
GuiInput signal
→ ConfirmCodeCompletion
→ direkt ytterligare InsertText
→ event stack fortfarande aktiv
```

onödigt aggressiv. Den andra mutationen låg utanför CodeEdits egen confirmation-transaktion men fortfarande inne i `Control::_call_gui_input`.

Den nuvarande deferred `InsertText` eliminerar just detta.

### TextChanged är deferred – vilket ändrar reentrancybilden

En särskilt viktig Godot 4.6.3-detalj finns i `TextEdit::_text_changed()`. En textmutation gör inte omedelbart:

```cpp
emit_signal("text_changed");
```

i samma insert-stack.

I stället markeras `text_changed_dirty`, och när TextEdit är i trädet schemaläggs `_emit_text_changed` deferred. `_emit_text_changed()` emitterar sedan `text_changed` och nollställer dirty-flaggan. `insert_text()` och `insert_text_at_caret()` har dessutom egna complex-operation scopes. fileciteturn35file7 fileciteturn35file0

Det betyder att den huvudsakliga risken inte är:

```text
InsertText
  → omedelbart nested TextChanged
      → Cancel
          → ...
```

utan snarare **ordering mellan flera deferred intents**.

Vid System Explorers nuvarande automatic-using-flöde sker exempelvis ungefär:

```text
ConfirmCodeCompletion
    ↓
TextEdit marks text_changed dirty
    ↓
Godot queues deferred _emit_text_changed
    ↓
Confirm returns

System Explorer queues deferred using insertion
    ↓

message queue later:
    Godot TextChanged emits
       → System Explorer sees using pending och suppressar/marks pending

    System Explorer using call
       → validates generation/binding
       → InsertText(using)
       → another text_changed can be queued

    final TextChanged
       → normal final-state validation
```

Det är en mycket bättre modell än synkron nested mutation. Men det visar samtidigt varför ett växande antal separata booleans blir svårt att bevisa korrekt: ordningen är nu en **deferred event protocol**.

Godots `CallQueue::flush()` är dessutom byggd för reentrancy; queue-offset pre-advances och nya messages kan köas under flush. `CallDeferred` ska därför främst ses som en **stack-unwind barrier**, inte som ett löfte om “en hel frame av isolation”. fileciteturn50file0L1-L2

### Request/cancel/add/update

Godot 4.6.3:s completion invariants är relativt enkla men viktiga:

`request_code_completion(false)` bestämmer först om den aktuella caret-kontexten kräver request och emitterar sedan `code_completion_requested`. Forced request emitterar direkt. fileciteturn65file0L2-L6

`add_code_completion_option()` lägger till i `code_completion_option_submitted`; `update_code_completion_options()` flyttar submitted-listan till source-listan, clear:ar submitted och filtrerar kandidaterna. Det innebär att publish logiskt är en samlad “add N options → update” operation. fileciteturn65file0L2-L6

`cancel_code_completion()` är inte bara kosmetik: den slår av `code_completion_active`, nollställer forced/drag state och redraw:ar. fileciteturn65file0L2-L6

`confirm_code_completion()` avslutar själv med `cancel_code_completion()` och kan omedelbart `request_code_completion()` igen om completionens sista tecken är ett completion-prefix. fileciteturn65file0L2-L6

Den sista detaljen är viktig för semantic/bare-dot senare: System Explorer får inte anta att “Confirm” alltid lämnar CodeEdit i permanent inactive completion state.

### Managed Callable kontra System Explorers named Callables

Här finns en av analysens starkaste avgränsningar.

Godot C# har två koncept som lätt blandas ihop.

En C#:

```csharp
new Callable(this, methodName)
```

konstrueras med ett `GodotObject` target och `StringName` method. `_delegate` och trampoline är null. fileciteturn52file0L1-L7

Det är exakt formen System Explorers reload-safe `TryConnectPluginSignal`, `IsPluginSignalConnected` och `DisconnectPluginSignal` använder. fileciteturn47file0L1-L7

En riktig delegate-backed `ManagedCallable` är däremot `CallableCustom` på C++-sidan med managed GCHandle + trampoline. Under hot reload håller Godot en lista över dessa. `CSharpLanguage::reload_assemblies()` försöker serialisera deras delegates, frigör gamla delegate handles före assembly reload och deserialiserar dem efter reload. fileciteturn38file0L1-L7 fileciteturn44file0

Det betyder:

**[Source-verifierad] Autocomplete-signalanslutningarna som går genom System Explorers named helper är inte stale `ManagedCallable`-trampolines.**

Det utesluter inte alla .NET-reloadproblem i hela pluginet. Andra features kan fortfarande använda delegates eller andra C# callbacks. Men det sänker kraftigt sannolikheten att just de namngivna autocomplete-signalerna kraschar native på grund av en gammal delegate function pointer.

### Named CallDeferred

Även här är skillnaden viktig.

Godots `CallQueue::push_callp(ObjectID, StringName, args...)` lägger en `Callable(ObjectID, method)` och kopierade `Variant`-argument på message queue. Vid flush hämtas targetobjektet från ObjectDB; om det fortfarande finns exekveras callable, annars hoppas operationen över. fileciteturn50file0L1-L2

Alltså håller:

```csharp
CallDeferred(nameof(ApplyDeferredAutocompleteScriptChangeRebind), token);
```

inte i sig en gammal System Explorer-delegate eller en gammal JIT-trampoline.

Detta är **bra för native safety men skapar en annan lifecycle-fråga**:

> Native pluginobjektet kan överleva en managed reload. En deferred method-by-name som köades av generation A kan därför ligga kvar tills targetobjektet nu representeras av generation B.

Därför är tokenkontroller fortfarande kritiska, även när Callable själv är reload-safe.

Den nuvarande automatic-using-vägen är särskilt bra här eftersom den explicit lagrar och verifierar både scheduled `ManagedAssemblyGeneration` och host instance token innan editorn muteras. Script-change-rebind och några andra deferred paths lutar mer mot tokens/transient reset. Den permanenta modellen bör göra **explicit assembly generation + host token + binding epoch** obligatorisk för *alla* editorrelaterade deferred commands.

## System Explorer mot Godots invariants, relaterade issues och rankade root causes

Följande sammanställning skiljer på sådant som redan ligger rätt och sådant som bör ändras.

| Område | Bedömning |
|---|---|
| `editor_script_changed` deferred rebind | **Rätt och bör permanentas.** Godot fortsätter i vissa transitioner efter signalen. **[Source-verifierad]** fileciteturn16file0 |
| Named signal Callables | **Rätt riktning.** De undviker delegate-backed ManagedCallable för de analyserade signalerna. **[Source-verifierad]** fileciteturn47file0L1-L7 fileciteturn52file0L1-L7 |
| Detach host before `Shutdown()` | **Rätt och bör behållas.** Callback lookup kan inte hitta hosten medan dess graph stängs ned. **[Source-verifierad]** fileciteturn22file0 |
| Cold host för stale generation | **Rätt.** Bör göras till en absolut generation invariant. |
| Native prefix/theme metadata | **Rätt grundidé.** Native objekt kan överleva managed graph; ownership behöver därför native representation. **[Source + loggstödd]** fileciteturn57file0L1-L2 |
| Normal rebind utan `CancelCodeCompletion()` | **Rätt permanent default.** Rebinding och cancellation bör vara separata operationer. |
| Deferred TextChanged validation utan cancellation | **Bra diagnostisk baseline.** Cancellation kan senare återinföras endast genom coordinator i stabil binding state. |
| `GuiInput` confirmation bridge | **Legitim användning av Godots inputmodell.** `gui_input`-signal emitteras uttryckligen före native handler för override/AcceptEvent. **[Source-verifierad]** fileciteturn59file0 |
| Synchronous secondary `InsertText` efter confirm | **Bör inte återinföras.** Current deferred path ger en starkare stack boundary. |
| Namespace Refactor quiescence | **Bra princip, för smal implementation.** Bör bli en generell mutation-ownership lease. |
| Många separata tokens/bools | **Arkitekturproblem.** De kodar redan en state machine men utan en enda ägare av transitionerna. |
| Workers | **Relativt välisolerade.** Gör “inga GodotObject i worker graph” till en explicit invariant. |

De diagnostiska isoleringarna kan därför bedömas individuellt:

**Semantic member pipeline disabled — [Arkitekturell inferens].** Jag ser inte evidens som visar att semantic pipeline i sig var assembly-reload-root cause. Däremot ökar den antalet completion requests, session transitions, native cancel/re-request och async semantic-resultat. Den bör förbli avstängd tills binding lease, central mutation coordinator och generation-bound publication är verifierade. Först därefter går det meningsfullt att A/B-testa semantic execution i stället för att låta den maskera lifecyclefel.

**Active-document syntax overlay disabled — [Arkitekturell inferens].** Samma princip. Overlay är framför allt farlig om worker/editor-capture-lagret får bära live `CodeEdit`/ScriptEditor-referenser eller publicera på en stale binding. Återaktivera efter att capture har definierats som “main-thread snapshot → pure managed data”.

**CancelCodeCompletion på rebind disabled — [Source-verifierad rekommendation].** Jag skulle inte gå tillbaka till den gamla generella modellen. Disconnect/rebind betyder “vi slutar äga callbacks/binding”, inte automatiskt “mutera den gamla CodeEdits completion state”. Cancellation bör ske endast som en explicit completion transaction när coordinatorn vet att samma CodeEdit fortfarande är stabil och ägs av den aktuella bindingen.

**Cancel under deferred TextChanged validation disabled — [Arkitekturell inferens].** Godots TextChanged är redan deferred, så detta är mindre riskabelt än cancellation från den synkrona InsertText-stacken hade varit. Men cancellation kan fortfarande konkurrera med en ny completion request, script transition eller deferred using. Återinför först efter serialization genom coordinator.

**Deferred ScriptEditor rebind — [Source-verifierad].** Permanent princip, inte tillfällig workaround.

**Deferred automatic-using InsertText — [Source-verifierad + inferens].** Permanent princip. Signal-before-native-input och AcceptEvent gör confirmation bridge legitim; den sekundära textmutationen tjänar däremot på att vänta tills input stacken unwindat.

**BeginComplexOperation/EndComplexOperation disabled — [Source-verifierad rekommendation].** `ConfirmCodeCompletion()` har redan en intern complex operation och `TextEdit.InsertText()` har en egen. En outer complex operation får **inte** öppnas i `GuiInput` och lämnas öppen över `CallDeferred`; det skulle låta undo-state leva över en message-queue boundary och över andra editorhändelser. fileciteturn65file0L2-L6 fileciteturn35file0

Om “completion + using = exakt en undo” senare är ett absolut UX-krav finns bara en principiellt ren väg med dagens API: båda mutationerna måste ägas av **samma senare editor transaction** och bracket:as där. Det skulle sannolikt kräva att även själva confirmationen flyttas ur GuiInput-signalen till en deferred command efter att eventet accepterats och att selected completion revalideras. Det är betydligt mer komplext och bör inte blandas in i lifecycle-stabiliseringen. Tills vidare är två säkra undo-operationer bättre än en undo-operation som kräver ett complex scope över frames.

**Namespace Refactor quiescence — [Source + loggstödd].** Embryot till en generell coordinator. Quiescence hindrar autocomplete men gör inte Namespace Refactors egna ScriptEditor/TextEdit-läsningar atomiska; CrashLog_11 slutar dessutom mitt i just den fasen. fileciteturn54file0L1-L7

### Relaterade Godot-problem

Godot issue **#102455, “Building C# solution leads to errors if signals are connected in the editor”**, dokumenterar problem efter C# rebuild med signaldelegates: reload kan lämna signal-/delegate-state i ett problematiskt läge och senare ge disposal/unload-problem. Det är en **stark analogi**, men inte en direkt match, eftersom System Explorers analyserade autocomplete-wiring använder object+method named Callables snarare än delegate-backed ManagedCallable. citeturn6view0

Godot-proposal **#9001, “Make the C# tool script reloading process easier to work with”**, dokumenterar hur tool scripts serialiseras, rekonstrueras och deserialiseras kring assembly reload och diskuterar uttryckligen behovet av att skjuta post-reload-arbete tills övriga scripts har återställts. Det är en **stark lifecycle-analogi** till System Explorers cold-generation/deferred-recovery-behov, men förslaget är inte runtime source of truth. citeturn8search1turn9search3

Issue **#87147** beskriver ett fall där en C# tool script som inte kan rekonstrueras korrekt efter build kan krascha editorn. Det är en **svagare analogi**: samma tool-script reload-domain, men inte samma ScriptEditor/CodeEdit/completion-väg. citeturn9search3

Jag hittade inte i det undersökta officiella Godot-materialet någon post-4.6.3 issue/PR som direkt identifierar och fixar exakt kombinationen `CodeEdit.ConfirmCodeCompletion`/`CancelCodeCompletion` + managed assembly reload. Därför finns det inget verifierat “Godot 4.7 har redan fixat just System Explorers failure mechanism” att basera roadmapen på. Det är en begränsning i issue-sökningen, inte bevis för att ingen närliggande fix kan existera.

### Rankade root-cause-hypoteser

| Rang | Hypotes | Bedömning |
|---|---|---|
| **Högst – fragmenterad editor mutation ownership över managed generations** | Flera System Explorer-subsystem äger delar av ScriptEditor/CodeEdit-livscykeln med separata barriers. Native editorobjekt lever längre än managed feature graph. En “valid now” CodeEdit kan bli logiskt stale mellan kontroll och mutation. | **Source-verifierad arkitekturell risk + loggstödd miljö.** Detta är den bästa förklaringsmodellen för den återkommande crash-klassen, men inte en bevisad enskild crash instruction. |
| **Mycket hög – reentry under ScriptEditor-transition** | Full rebind/native mutation från `editor_script_changed` kan konkurrera med fortsatt Godot `ScriptEditorBase::validate()`/editor transition. | **Source-verifierad risk.** Current deferred isolation adresserar detta korrekt. |
| **Hög historisk risk – flera CodeEdit-mutationer inne i GuiInput-stack** | Confirm är redan en stor native transaction. Historisk synchronous secondary InsertText, cancellation/rebind eller andra native mutations innan inputstack unwind ökar reentrancyytan. | **Source-verifierad call path + arkitekturell inferens.** Current deferred using minskar risken kraftigt. |
| **Medel – deferred intent över assembly generation** | Named CallDeferred är pointer-safe men en gammal queued method-by-name kan potentiellt nå ett nytt managed pluginobjekt om inte generation/token avvisar den. | **Source-verifierad mekanism + inferens.** |
| **Medel för CrashLog_11 specifikt – Namespace Refactor buffer traversal** | Sista loggen ligger i `BuildCompleteOpenEditorGroups`, efter ScriptEditor control scan och före ytterligare TextEdit state/text-inspektion. | **Starkt loggstödd korrelation, inte kausalitet.** |
| **Låg/medel – worker/ALC lifetime** | Gamla Tasks kan fortsätta tills cancellation observeras och hålla gammal ALC vid liv. | **Source-verifierat managed fenomen**, men current publication/lifetime guards är starka och CrashLog_11 visar noll workers vid sista unloaden. |
| **Låg för analyserade autocomplete-signaler – stale ManagedCallable** | Native signal skulle hålla gammal delegate/trampoline. | **Motbevisad som generell förklaring för autocomplete-wiringen:** den använder named target/method Callables. Delegate-issues är fortfarande relevanta för andra event paths. |

Den övergripande slutsatsen blir därför att **native hard crash safety inte främst ska byggas genom fler exception guards**. En stale pure-managed Roslyn-operation ger normalt cancellation/exception/unload failure. Hard-crash-klassen blir plausibel när gammal eller felordnad managed kod får passera GodotObject/native-gränsen under en editortransition. Det är den gränsen arkitekturen måste göra exklusiv och verifierbar.

## Permanent architecture roadmap och lifecycle-state-machine

Målarkitekturen bör separera tre livstider som dagens kod delvis men inte fullständigt separerar:

```text
Native editor lifetime
    ScriptEditor / ScriptEditorBase / CodeEdit
    kan överleva många C# rebuilds
          │
          │ bindning genom explicit BindingEpoch
          ▼
Managed autocomplete generation
    AutocompletePluginHost
    completion services
    editor binding
    feature coordinators
    SKA vara kall per ManagedAssemblyGeneration
          │
          │ pure-data requests/results
          ▼
Analysis worker lifetime
    Roslyn / project index / syntax / semantic workers
    INGA GodotObject-referenser
    cancellation + generation-bound publication
```

Ovanpå detta bör en enda main-thread `AutocompleteEditorLifecycleCoordinator` — namnet är inte viktigt, ansvaret är det — vara **enda instansen som får auktorisera CodeEdit-native mutation**.

Den implicita state machine som dagens bools representerar bör göras explicit ungefär så här:

| State | Tillåtna operationer | Förbjudna operationer |
|---|---|---|
| `Detached` / `HostRetired` | Managed cleanup, cancellation | Alla CodeEdit-muteringar |
| `ReloadQuiescent` | Avbryt workers, invalidera tokens, detach host | Confirm, Cancel, Request, Insert, completion publish, rebind mutation |
| `RecoveringGeneration` | Verifiera native objects, återställ orphan ownership, komponera ny host | Feature execution innan binding etablerats |
| `BindingPending` | Läs current ScriptEditor/current editor/base editor; skapa ny binding lease | Completion mutation mot gammal/oklar CodeEdit |
| `Stable` | Request/publish completion och schemalägga kontrollerade transactions | Direkt feature-ägd mutation utanför coordinator |
| `ScriptTransitionPending` | Markera/coalesca nästa binding | Rebind, cancel, theme/prefix mutation i signalstacken |
| `CompletionInput` | Inspect selected option; en ägd confirmation; AcceptEvent | Rebind, secondary InsertText, independent Cancel |
| `MutationPending` | Bara token/binding validation | Native mutation innan alla guards gått igenom |
| `MutationExecuting` | Exakt den auktoriserade native transaktionen | Andra konkurrerande editor mutations |
| `ExternalMutationQuiescent` | Registrera pending autocomplete-intents | Autocomplete-native mutation; Namespace/annan feature äger editorn |

Varje etablerad editorbindning bör få en immutable identity/lease ungefär:

```text
EditorBindingLease
{
    ManagedAssemblyGeneration
    HostInstanceToken
    ScriptTransitionId
    BindingEpoch
    ScriptEditorInstanceId
    ScriptEditorBaseInstanceId
    CodeEditInstanceId
    ScriptResourcePath
}
```

Den centrala regeln blir:

> Ingen `ConfirmCodeCompletion`, `CancelCodeCompletion`, `RequestCodeCompletion`, `Add/UpdateCodeCompletionOptions`, `InsertText`, caretmutation, prefix/theme/meta-mutation eller signal-rebind får exekveras från en feature bara för att `TryGetActiveCodeEdit()` nyss returnerade true. Den måste exekveras som en coordinator-owned transaction vars lease verifieras omedelbart före native callen.

### Implementationsstatus efter ScriptEditor lifecycle foundation

**Nu permanent implementerat:** `ManagedAssemblyGeneration + ScriptTransitionId + BindingEpoch` som explicit ScriptEditor/CodeEdit binding-lifecycle. `ScriptEditorLifecycleCoordinator` är pure-managed, System Explorer-navigation registrerar transition före `EditScript`, och `editor_script_changed` observeras före managed recovery/feature work. Notifications får coalesca inom samma `BindingPending` transition när den normaliserade authoritative target-pathen är oförändrad, så samma lifecycle-target behåller samma `ScriptTransitionId`; en verklig target-förändring supersedar transitionen, och `Stable + same path` coalescas inte generellt eftersom ScriptEditorBase/CodeEdit kan ha bytts. Föregående lease blir logiskt stale direkt när en ny transition faktiskt börjar, och endast den generation-/host-/operation-/transition-bundna deferred stack-unwind-resolvern får etablera en ny CodeEdit-binding och commit:a nästa `BindingEpoch`. Feature paths kan endast använda en current `Stable` lease eller lämna ett rebind-intent till samma resolver. Normal lifecycle-rebind behåller `cancelNativeCompletionOnRebind=false`.

**Fortfarande nästa arkitektursteg:** en enda CodeEdit mutation/transaction coordinator. Den nu implementerade lifecycle-coordinatorn avgör *vilken binding som är current*, men `RequestCompletion`, `CancelCompletion`, `ConfirmCompletion`, `InsertText`, publish och prefix/theme/native-presentation mutation är ännu inte samlade under en serialiserad mutation transaction. Roadmapens bredare mutation-states nedan beskriver därför fortfarande målarkitekturen efter denna foundation.

### Roadmap

| Steg | Mål, konkret problem och evidens | Berörda klasser, arkitektur och Godot-invariant | Risk, verifieringsgate och isolationsbeslut |
|---|---|---|---|
| **Generation-bound deferred envelope** | **Mål:** gör all deferred autocomplete work bevisbart ofarlig över rebuild. **Problem:** named `CallDeferred` håller ObjectID+method, inte gammal delegate; gammal intent kan därför semantiskt korsa managed generation. **Evidens:** Godot `CallQueue` + dagens blandade tokenstrategier. **[Source-verifierad]** fileciteturn50file0L1-L2 | Berör `SystemExplorerPlugin.Autocomplete.cs`, reload lifecycle och alla named deferred autocomplete-metoder. Varje command får `{assembly generation, host token, operation token, senare binding epoch}` och gör **första** kontrollen innan någon ScriptEditor/CodeEdit-access. Centralisera invalidation. **Ändra inte** completion features, semantic pipeline eller using UX i detta steg. | **Risk:** låg. **Verifiering:** queue varje deferred path och trigga omedelbart rebuild; den gamla commanden måste logga `RejectedStaleGeneration` före första editor call. Nästa steg kräver noll cross-generation native calls i stresslogg. Alla nuvarande feature-isolations behålls. |
| **Hård cold-generation boundary** | **Mål:** göra varje rebuild till ett verkligt autocomplete-generation byte. **Problem:** native editorobjekt får överleva, men managed feature graph ska inte ha oklart återbruk. | `EditorReloadLifecycle`, `AutocompletePluginHost`, `AutocompleteIndexLifetime`. Vid generation mismatch: bump tokens → mark `ReloadQuiescent` → stop accepting worker work → detach host → shutdown detached host → restore/recover native reversible state → cold-compose host → senare bind. Full dock rebuild endast fallback. **Ändra inte** hela System Explorer UI-recoveryn. | **Risk:** medel på grund av recovery-order. **Verifiering:** minst 100 sequential rebuilds med samma native ScriptEditor/CodeEdit över flera generations; exakt en current host, inga gamla publications. Nästa gate: varje loggad native autocomplete call kan hänföras till current generation. |
| **Explicit ScriptEditor/CodeEdit binding state machine — IMPLEMENTERAD FOUNDATION** | **Permanent implementerat:** implicit callback-depth/bool-logik är ersatt som correctness authority av `ManagedAssemblyGeneration + ScriptTransitionId + BindingEpoch`. System Explorer-navigation börjar transition före `EditScript`; `editor_script_changed` observerar expected/external path; repeated same-target notifications i `BindingPending` behåller samma transition medan en verklig target-förändring supersedar den; varje faktiskt ny transition gör outgoing lease stale direkt. | `EditorIntegration/ScriptEditing/ScriptEditorLifecycleCoordinator`, `SystemExplorerPlugin.ScriptEditorLifecycle`, `ScriptOpening`, `ScriptEditorSync`, `SystemExplorerPlugin.Autocomplete`, `AutocompletePluginHost`, `AutocompleteEditorBinding`. Latest-wins deferred resolver verifierar generation + host + operation token + current transition före editor access och är enda CodeEdit binding-establishment path. Immutable lease innehåller native IDs/path; stale candidate rollback publiceras aldrig. Normal rebind **cancel:ar inte native completion**. | **Verifieringsgate kvar i Godot:** långsam/snabb/filtered/held-arrow/Create Script/external ScriptEditor navigation samt rebuild-stress ska visa att outgoing BindingEpoch aldrig blir current igen. Den statiska arkitekturprincipen är permanent; nästa roadmap-steg är mutation/transaction coordinator, inte ytterligare feature-owned rebind. |
| **En enda CodeEdit mutation/transaction coordinator** | **Mål:** ta bort konkurrerande mutation ownership. **Problem:** request/cancel/confirm/insert/prefix/theme/meta kommer idag från olika feature paths. | Ny coordinator eller tydligt förstärkt editor lifecycle-klass. Alla native mutating intents går genom den: `PublishCompletion`, `RequestCompletion`, `CancelCompletion`, `ConfirmCompletion`, `InsertText`, presentation ownership apply/restore. En operation har current binding lease och kör main-thread/serialized. Namespace Refactor får en generell `ExternalMutationLease`. **Ändra inte** semantic algorithms eller completion ranking. | **Risk:** högst i roadmapen eftersom kontrollflöde centraliseras, men förändringen kan göras utan att ändra features. **Verifiering:** instrumentation ska kunna assert:a “ingen native mutation utan current transaction ID + binding epoch”. Nästa steg först när detta håller under rebuild, script switching och Namespace Refactor. |
| **Completion transaction protocol** | **Mål:** definiera exakt hur popup-livscykeln får påverkas. **Problem:** CodeEdit confirmation muterar text/caret/completion och kan själv cancel/re-request; TextChanged är deferred. | Confirmation bridge får fortsatt fånga System Explorer-option via `gui_input` signal, göra **exakt en** confirm och `AcceptEvent`. Secondary using förblir deferred och går genom coordinator. TextChanged-validation producerar en `CancelIntent` snarare än att direkt cancel:a. `AddCodeCompletionOption* + UpdateCodeCompletionOptions` behandlas som en atomisk publish-intent. | **Risk:** medel. **Verifiering:** popup open/typing/accept/cancel/re-request/bare-prefix över rebuild och scriptbyte; exakt en confirm per accepted System Explorer-option. Efter detta kan safe cancellation försiktigt återaktiveras. `automaticUsingComplexOperationWrapper` förblir false. |
| **Worker/index isolation som formell invariant** | **Mål:** göra managed unload-problem strukturellt oförmögna att bli native crash. **Problem:** Task kan fortsätta gammal managed kod tills cancellation observeras. | `AutocompleteIndexLifetime`, project/active-document/semantic coordinators och workers. Worker graph får aldrig innehålla `GodotObject`, `ScriptEditor`, `CodeEdit`, plugin instance eller managed wrapper. Main thread fångar immutable text/path/caret snapshot. Resultat är pure DTO och kan endast publiceras via current generation+binding gate. Roslyn objects dör med generationen. | **Risk:** låg/medel beroende på befintliga semantic paths. **Verifiering:** artificiellt lång worker + rebuild medan den arbetar; gamla worker får avsluta managed men aldrig publish eller röra Godot. Nästa gate: ALC unload stabil utan old-generation publications. |
| **Reversible native presentation ownership** | **Mål:** behålla den goda metadata-idén men göra den coordinator-owned. **Problem:** prefix/theme överlever managed generation och kan annars bli orphan eller clobbra annan ägare. | `AutocompleteCodeEditNativeOwnershipBridge` + binding. Marker behåller owner generation och native ID; lägg binding epoch/versioning till ownership-protokollet. Restore görs endast i en stabil binding/recovery transaction och helst compare-before-restore så System Explorer inte skriver över ett värde som ändrats efter att det tog ownership. **Ändra inte** completion semantics. | **Risk:** låg. **Verifiering:** rebuild med samma CodeEdit, nytt CodeEdit, stängt script och theme change; ingen orphan marker, ingen felaktig restore. CrashLog_11 visar redan att cross-generation recovery-principen fungerar. fileciteturn57file0L1-L2 |
| **Feature reactivation ovanpå den stabila grunden** | **Mål:** återfå full autocomplete utan att återöppna lifecycle-yta. | Feature för feature; inga genvägar runt coordinator. | Se nästa avsnitt. |

Detta är avsiktligt **inte** ett “rewrite everything”-steg. De första stegen kan införas medan project-type autocomplete fortfarande är den enda funktionella baselinen. Därmed får varje lager ett eget falsifierbart testresultat.

## Närmaste diagnostik och feature reactivation

Den permanenta `ManagedGeneration + ScriptTransition + BindingEpoch`-foundation är nu implementerad, inklusive invarianten att repeated same-target notifications kan coalesca inom en current `BindingPending` transition medan en verklig target-förändring supersedar den. Närmaste arbete är därför att verifiera lifecycle-invarianten i Godot-stresslogg och därefter ta roadmapens separata CodeEdit mutation/transaction coordinator; undvik att återgå till feature-owned binding recovery eller ytterligare lokala rebind-authorities.

Tre diagnostiska tester har fortfarande tillräckligt högt informationsvärde för att vara motiverade parallellt med verifieringen och den fortsatta mutation-coordinator-refaktorn.

**Diagnostik A – CrashLog_11 boundary isolation.** Instrumentera Namespace Refactor buffer locator kring varje editor-boundary *efter* den nuvarande sista loggraden: före/efter `IsUnsaved`, före/efter `TextEdit.Text`/motsvarande text read, native TextEdit ID och den ScriptEditorBase som buffern kommer från. Kör sedan A/B där non-current open-editor scanning stängs av men övrig Namespace Refactor-process lämnas identisk. Om crashsignaturen försvinner över tillräckligt många repetitioner har ni isolerat ett problem som är bredare än autocomplete. Detta är det högst värderade testet för **CrashLog_11 specifikt**. fileciteturn54file0L1-L7

**Diagnostik B – cross-generation deferred sentinel.** Se till att var och en av script-rebind, TextChanged-validation, automatic using och lifecycle recovery har en pending named `CallDeferred`, och starta build innan den körs. Förväntningen ska vara “new generation rejects old operation före första Godot editor read/write”. Detta verifierar den centrala CallDeferred-slutsatsen direkt.

**Diagnostik C – rebuild medan worker verkligen arbetar.** Tvinga ett långvarigt project/semantic-indexjobb, rebuild mitt i jobbet och observera `AssemblyLoadContext.Unloading`, cancellation, worker drain och publication. Den gamla workern får gärna fortsätta ren managed cleanup tills cancellation når den; den får aldrig publicera till ny host och aldrig göra Godot-access. Detta avgör om den nuvarande lifetime-modellen är lika stark under faktisk active-worker unload som den ser ut i koden.

Inget av dessa tester behöver blockera arbetet med den permanenta coordinatorn.

### Feature reactivation

**Project-type completion baseline** förblir första referensläget. Den får köras när generation, binding epoch och mutation coordinator är stabila. Stressa popup open + rebuild + rapid script switching. Rollback om en completion publish eller confirm går mot en stale epoch, även om ingen crash sker.

**Request/cancel behavior** är nästa steg. Återaktivera native cancellation först som coordinator-owned `CancelCompletion` i `Stable` state. Förbjud cancellation i `ScriptTransitionPending`, `ReloadQuiescent`, rebind disconnect och `ExternalMutationQuiescent`. Stressa typing/backspace/caret movement och rebuild medan popup är öppen. Rollback vid dubbel cancel/request-loop, popup som tillhör fel script eller mutation mot outgoing CodeEdit.

**Automatic using** integreras därefter permanent i transaction coordinator. Behåll:

```text
GuiInput
→ one Confirm
→ AcceptEvent
→ callback returns
→ deferred generation/binding validation
→ one InsertText
```

Låt `BeginComplexOperation/EndComplexOperation` fortsätta vara avstängt. Stressa hundratals project-type accepts, rebuild omedelbart efter confirm och script switch före deferred insertion. Rollback om en using kan hamna i annat script/annan CodeEdit eller om mer än en insertion förekommer.

**Active-document syntax overlay** kan sedan återaktiveras. Förutsättningen är att active-document capture är main-thread och kopierar ren text/path/caret-version; worker/coordinator får inte behålla CodeEdit. Overlay-resultat måste bära `{assembly generation, document version, binding epoch}`. Rollback på varje stale-result publish.

**Semantic member autocomplete** kommer först efter overlay eftersom det skapar fler samtidiga completion states och re-request/cancel-situationer. Samma immutable snapshot/worker/result-regler gäller. Semantic source får aldrig själv äga CodeEdit-mutation; den lämnar kandidater/intents till main-thread coordinatorn. Rollback vid stale semantic publication, popup från föregående dokument eller request/cancel-loop.

**Bare-dot/dormant follow-up** kommer efter semantic baseline. Detta är särskilt viktigt eftersom Godots egen `confirm_code_completion()` kan request:a ny completion när den accepterade textens sista tecken är ett completion-prefix. System Explorers dormant recovery måste därför fungera tillsammans med Godots native re-request-beteende, inte anta exklusivt ägande. fileciteturn65file0L2-L6

**Richer autocomplete** — kombinerade semantic/project/overlay-features, mer aggressiv refresh och andra convenience-funktioner — kommer sist. En ny feature ska inte få introducera en ny direkt `CodeEdit.*` mutation path. Det är den viktigaste arkitekturregeln för att förhindra återfall.

## Stress-testprotokoll och öppna frågor

Efter varje större lifecycle-steg bör testningen ske i **samma Godot-process över många rebuilds**. En av de centrala observationerna i CrashLog_11 är just att problemen uppträder efter en långlivad editorprocess med flera managed generations; ett test som startar om Godot efter varje build missar därför den viktigaste failure mode. fileciteturn11file0L35660-L35742

En praktisk testmatris är:

| Scenario | Vad som ska göras under rebuild |
|---|---|
| Idle | Build upprepade gånger utan öppet completion state |
| C# script open | Current ScriptEditorBase/CodeEdit ska överleva där Godot tillåter det |
| Typing | Rebuild mitt under snabb textinmatning |
| Popup open | Rebuild med native completion popup aktiv |
| Precis efter confirmation | Acceptera System Explorer-option och build omedelbart |
| Automatic using pending | Försök träffa fönstret mellan confirm och deferred insertion |
| Rapid script switching | Växla mellan flera C# scripts medan builds görs |
| System Explorer navigation | Navigera tree → scripts medan ScriptEditor changes coalescas |
| Namespace Refactor | Kör operationen före/efter build och vid filesystem refresh |
| Filesystem/index activity | Build medan index request/worker är aktiv |
| Focus out/in | Alt-tab eller motsvarande under reload och completion |
| Minimize/restore | Rebuild/minimize/restore i samma process |
| Script close/open | Stäng current editor under pending deferred work |
| Popup + script switch | Öppna completion och byt script innan follow-up-operation |
| Worker active | Avsiktligt lång worker och build före completion |

För varje test ska loggen kunna svara på minst:

```text
ManagedAssemblyGeneration
HostInstanceToken
BindingEpoch
ScriptEditor native ID
ScriptEditorBase native ID
CodeEdit native ID
current script path
LifecycleState
MutationTransactionId
callback depth
pending deferred command generation/token
worker lifetime generation
quiescence owner/reason
```

Det är en viktig förbättring över att bara logga många booleans: man ska kunna ta **vilken native mutation som helst** och säga exakt vilken generation, binding och transaction som auktoriserade den.

För repetitionsantal bör testningen ha flera nivåer. Ett utvecklings-smoke kan vara ungefär 10 repetitioner per berörd scenario efter ett lokalt steg. En riktig lifecycle gate bör däremot omfatta **minst cirka 300 relevanta adversarial cycles utan hard crash eller stale-native-operation**, fördelade över flera kombinationer i samma långlivade editorprocess. Före full semantic reactivation skulle jag dessutom köra minst 100 rena sequential assembly rebuilds i samma process. För release-confidence när ett tidigare fel varit mycket intermittent är **500–1000 blandade rebuild/editor-mutation cycles över flera färska editorprocesser** rimligare.

Som statistisk intuition: om oberoende försök ger noll fel är den klassiska “rule of three” ungefär `3/N` som övre 95-procentsgräns för failure probability. Noll fel på 100 körningar säger därför inte särskilt mycket om ett 1%-problem; noll på 300 börjar vara betydligt mer informativt. Editorhändelserna är inte verkligt oberoende, så siffrorna ska användas som testheuristik, inte som formell tillförlitlighetsbevisning.

**Observationen som ska krävas före nästa roadmap-lager är inte bara “Godot kraschade inte”.** Den ska vara:

> Under hela testet förekom ingen native editor mutation som saknade current managed generation, current binding epoch och exklusiv mutation transaction.

Det gör intermittent frånvaro av crash till sekundär evidens i stället för enda beviset.

De viktigaste öppna frågorna är följande.

**CrashLog_11:s exakta native fault instruction är fortfarande okänd.** Sista managed breadcrumb ligger i Namespace Refactor buffer scanning, men utan native stack/dump går det inte att kalla den operationen root cause. Den bör därför följas upp separat och får inte användas som bevis för att current autocomplete confirmation path kraschar.

**Det är inte verifierat att varje callback i hela System Explorer använder named object/method Callable.** Autocomplete- och de analyserade reload-safe plugin-signalhelpers gör det. Godot-issue #102455 visar att delegate-baserade signaler har en reell annan reloadproblematik, så en separat inventering av alla `+=`, `Callable.From`, lambda/delegate-signalconnections i pluginet är fortfarande motiverad innan man deklarerar hela pluginets signalgraph reload-safe. citeturn6view0

**Active-document och semantic workers behöver samma fullständiga no-GodotObject-audit som project-index-lifetime.** Den studerade project-indexmodellen är stark; det är inte samma sak som ett formellt bevis att varje framtida semantic continuation är ren managed kod.

**En enda undo-operation för completion + automatic using står i konflikt med den säkraste nuvarande stack-barriären.** Godot 4.6.3 bracketer redan både confirmation och InsertText med egna complex operations. Att hålla ett outer scope öppet över `CallDeferred` är inte en acceptabel permanent lösning. Om single-undo senare är nödvändigt bör det behandlas som en separat feature-design efter lifecycle-stabilitet, sannolikt genom en enda deferred mutation transaction — inte genom att återinföra synchronous secondary mutation.

**Den viktigaste permanenta arkitekturprincipen är däremot tillräckligt väl underbyggd för implementation utan fler breda crash-isoleringar:**

```text
Managed rebuild
    ↓
ReloadQuiescent
    ↓
invalidate all generations / deferred work
    ↓
detach + retire old managed host
    ↓
cancel/drain pure-managed workers
    ↓
recover reversible state on surviving native objects
    ↓
cold-compose new managed host
    ↓
deferred ScriptEditor resolution
    ↓
new BindingEpoch
    ↓
Stable
    ↓
all CodeEdit mutation only through one transaction coordinator

editor_script_changed
    ↓
record transition intent only
    ↓
return to Godot
    ↓
deferred latest-wins rebind
    ↓
new BindingEpoch

GuiInput project-type accept
    ↓
one confirmation
    ↓
AcceptEvent
    ↓
return to Godot
    ↓
generation/binding-bound deferred secondary mutation

Namespace/other editor mutation
    ↓
acquire generic ExternalMutationLease
    ↓
autocomplete records pending intents only
    ↓
external mutation and its required deferred editor work complete
    ↓
release lease
    ↓
one consolidated autocomplete resync
```

Det är den arkitektur som bäst följer vad både **System Explorer commit `30a6b6cf2fd18b9c3e8b8472f42d4911004fef3a`** och **Godot 4.6.3 commit `35e80b3a8822a9df9be390814b62f44c0a9c69e8`** faktiskt visar: native editorobjekt kan leva mycket längre än den managed kod som använder dem; Godot har egna pågående editor- och completion-transaktioner; signaler och deferred work är notification/queue boundaries, inte ownership locks; och native hard-crash-säkerhet kräver därför att System Explorer gör **generation, binding och mutation ownership explicit och centraliserad innan richer autocomplete återaktiveras**. fileciteturn0file0L1-L13 fileciteturn5file0L1-L13