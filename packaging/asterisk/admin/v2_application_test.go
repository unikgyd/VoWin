package main

// Offline controller/filesystem tests only. Mutating/observing child transports
// are synthetic; these do not claim to execute Python, Docker, install or PBX.
import (
	"context"
	"encoding/json"
	"errors"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func applicationTestOptions()v2ApplicationOptions {return v2ApplicationOptions{Enabled:true,Image:"sha256:"+strings.Repeat("b",64),RestartPolicy:"unless-stopped",ConfirmManagement:true}}
func applicationTestController(t *testing.T)(*manager,*v2ApplicationController) {
	t.Helper();if runtime.GOOS!="linux"||os.Geteuid()!=0{t.Fatal("controlled application filesystem tests require the declared native Linux root runner")}
	root:=t.TempDir();base,store,helper:=filepath.Join(root,"base"),filepath.Join(root,"draft"),filepath.Join(root,"helpers")
	for _,path:=range []string{base,store,helper}{if e:=os.Mkdir(path,0700);e!=nil{t.Fatal(e)}}
	for _,name:=range []string{"configure.py","admin_apply_v2.py"}{if e:=os.WriteFile(filepath.Join(helper,name),[]byte("# Synthetic transport-only fixture; never executed.\n"),0600);e!=nil{t.Fatal(e)}}
	m:=&manager{base:base,v2Store:store,generator:filepath.Join(helper,"configure.py"),container:"synthetic-test-target"}
	c,e:=newV2ApplicationController(m,applicationTestOptions());if e!=nil{t.Fatal("synthetic controller initialization failed")};m.application=c
	c.mutate=func([]string,*os.File)([]byte,error){return nil,errors.New("unexpected synthetic mutation")}
	c.observe=func(context.Context,[]string)([]byte,error){return applicationTestEnvelope(applicationTestOverview(nil)),nil}
	t.Cleanup(func(){if c.Close()!=nil{t.Error("test left an active worker")}});return m,c
}
func applicationTestEnvelope(value any)[]byte {raw,_:=json.Marshal(map[string]any{"ok":true,"value":value});return raw}
func applicationTestOperation(state,operation,revision string)*v2ApplicationOperation {return &v2ApplicationOperation{Operation:&operation,Revision:&revision,State:state,FileCount:20,ChangedFiles:[]string{"config/pjsip.conf"},PBXApplied:state=="applied",StatusSource:"journal"}}
func applicationTestOverview(operation *v2ApplicationOperation)v2ApplicationOverview {return v2ApplicationOverview{SchemaVersion:1,Operation:operation,InstalledState:"unknown",StatusSource:"journal"}}
func applicationTestBody(action,request,revision,operation string)string {value:=map[string]any{"request":request,"confirm":true};if action!="recover"{value["revision"]=revision};if action!="prepare"{value["operation"]=operation};raw,_:=json.Marshal(value);return string(raw)}
func applicationTestHTTP(t *testing.T,m *manager,method,path,body string)*httptest.ResponseRecorder {t.Helper();s:=&webServer{m:m};w:=httptest.NewRecorder();r:=httptest.NewRequest(method,path,strings.NewReader(body));if !s.handleV2Application(w,r){t.Fatal("application route not handled")};return w}
func applicationTestWait(t *testing.T,c *v2ApplicationController) {t.Helper();c.mu.Lock();active,done:=c.active,c.done;c.mu.Unlock();if !active{return};select{case <-done:case <-time.After(2*time.Second):t.Fatal("synthetic worker did not finish")}}
func applicationTestResponse(t *testing.T,w *httptest.ResponseRecorder)v2ApplicationResponse {t.Helper();var value v2ApplicationResponse;if !applicationJSON(w.Body.Bytes(),&value,65536){t.Fatal("response is not the declared DTO")};return value}

func TestAdminV2ApplicationDisabledDoesNotTouchFilesystemOrRunHelper(t *testing.T) {
	m:=&manager{base:filepath.Join(t.TempDir(),"absent")};c,e:=newV2ApplicationController(m,v2ApplicationOptions{});if e!=nil||c!=nil{t.Fatal("disabled capability must be inert")}
	w:=applicationTestHTTP(t,m,"GET","/api/v2/application","");value:=applicationTestResponse(t,w);if w.Code!=200||value.Capability.Enabled||value.Overview!=nil||value.Job!=nil{t.Fatal("disabled DTO differs")}
	if w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",`{}`);w.Code!=403{t.Fatal("disabled application accepted mutation")}
	if _,e=os.Stat(m.base);!errors.Is(e,os.ErrNotExist){t.Fatal("disabled capability created a private path")}
}
func TestAdminV2ApplicationConstructorRequiresExplicitServerOwnedEnrollment(t *testing.T) {
	m,c:=applicationTestController(t);if c.Close()!=nil{t.Fatal("close failed")}
	for _,change:=range []func(*v2ApplicationOptions){func(o *v2ApplicationOptions){o.ConfirmManagement=false},func(o *v2ApplicationOptions){o.Image="latest"},func(o *v2ApplicationOptions){o.RestartPolicy="always"}}{
		o:=applicationTestOptions();change(&o);next,e:=newV2ApplicationController(m,o);if e==nil||next!=nil||strings.Contains(e.Error(),m.base){t.Fatal("unsafe enrollment accepted or reflected a private path")}
	}
}
func TestAdminV2ApplicationRequestsAreExactAndNeverAcceptCallerTargetOrCommands(t *testing.T) {
	binding:=strings.Repeat("c",64);request,revision,operation:=strings.Repeat("R",43),strings.Repeat("V",43),strings.Repeat("O",43)
	for _,action:=range []string{"prepare","apply","recover"}{raw:=applicationTestBody(action,request,revision,operation);if _,_,ok:=applicationRequest([]byte(raw),action,binding);!ok{t.Fatal("valid exact action rejected")}}
	good:=applicationTestBody("prepare",request,revision,"")
	cases:=[]string{`{}`,`null`,strings.Replace(good,`"confirm":true`,`"confirm":null`,1),strings.Replace(good,`"confirm":true`,`"confirm":false`,1),strings.Replace(good,`"confirm":true`,`"confirm":"true"`,1),strings.Replace(good,`"request":`,`"Request":`,1),strings.TrimSuffix(good,"}")+`,"command":"secret-must-not-run"}`,strings.TrimSuffix(good,"}")+`,"image":"caller-controlled"}`,strings.TrimSuffix(good,"}")+`,"request":"`+request+`"}`,strings.Replace(good,revision,"short",1),good+`{}`,strings.Repeat(" ",65537)}
	for i,raw:=range cases{t.Run(string(rune('a'+i)),func(t *testing.T){if _,_,ok:=applicationRequest([]byte(raw),"prepare",binding);ok{t.Fatal("unsafe request accepted")}})}
}
func TestAdminV2ApplicationAsyncCancellationAndIdempotentAttach(t *testing.T) {
	m,c:=applicationTestController(t);request,revision:=strings.Repeat("R",43),strings.Repeat("V",43);operation:=strings.Repeat("O",43)
	started,release:=make(chan struct{}),make(chan struct{});var once sync.Once;var runs atomic.Int32
	defer func(){once.Do(func(){close(release)});applicationTestWait(t,c)}()
	c.mutate=func(args []string,lease *os.File)([]byte,error){runs.Add(1);if lease==nil||!reflect.DeepEqual(args,c.cliArgs("prepare",applicationPayload{Revision:revision})){t.Error("worker did not use fixed server-owned args/lease")};close(started);<-release;return applicationTestEnvelope(applicationTestOperation("prepared",operation,revision)),nil}
	body:=applicationTestBody("prepare",request,revision,"");ctx,cancel:=context.WithCancel(context.Background());r:=httptest.NewRequest("POST","/api/v2/application/prepare",strings.NewReader(body)).WithContext(ctx);w:=httptest.NewRecorder();if !(&webServer{m:m}).handleV2Application(w,r)||w.Code!=202{t.Fatal("job was not durably queued")};cancel()
	select{case <-started:case <-time.After(2*time.Second):t.Fatal("worker did not start")}
	if w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body);w.Code!=202{t.Fatal("same request did not attach")}
	if w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",applicationTestBody("prepare",request,strings.Repeat("X",43),""));w.Code!=409{t.Fatal("request ID reused for another payload")}
	if w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",applicationTestBody("prepare",strings.Repeat("S",43),revision,""));w.Code!=409{t.Fatal("second worker was admitted")}
	once.Do(func(){close(release)});applicationTestWait(t,c);w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body);value:=applicationTestResponse(t,w)
	if w.Code!=200||runs.Load()!=1||value.Job==nil||value.Job.State!="succeeded"||value.Job.Operation!=operation||value.Job.Result==nil||value.Job.Result.State!="prepared"{t.Fatal("cancellation/idempotency/preflight result differs")}
	if value.Job.Result.PBXApplied||value.Overview!=nil{t.Fatal("prepare was mislabeled as a complete PBX application")}
	if _,e:=time.Parse(time.RFC3339Nano,value.Job.CreatedAt);e!=nil||!strings.HasSuffix(value.Job.CreatedAt,"Z"){t.Fatal("job timestamp is not RFC3339 UTC")}
}
func TestAdminV2ApplicationDurableBindingSurvivesControllerRestart(t *testing.T) {
	m,c:=applicationTestController(t);request,revision,operation:=strings.Repeat("R",43),strings.Repeat("V",43),strings.Repeat("O",43)
	c.mutate=func([]string,*os.File)([]byte,error){return applicationTestEnvelope(applicationTestOperation("prepared",operation,revision)),nil}
	body:=applicationTestBody("prepare",request,revision,"");if applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body).Code!=202{t.Fatal("enqueue failed")};applicationTestWait(t,c);if c.Close()!=nil{t.Fatal("close failed")}
	next,e:=newV2ApplicationController(m,applicationTestOptions());if e!=nil{t.Fatal("reopen failed")};defer next.Close();m.application=next;var runs atomic.Int32;next.mutate=func([]string,*os.File)([]byte,error){runs.Add(1);return nil,errors.New("must not replay")}
	w:=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body);if w.Code!=200||applicationTestResponse(t,w).Job.State!="succeeded"||runs.Load()!=0{t.Fatal("restart replayed or forgot durable binding")}
	if w=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",applicationTestBody("prepare",request,strings.Repeat("X",43),""));w.Code!=409{t.Fatal("restart forgot payload conflict")}
}
func applicationTestStage(t *testing.T,c *v2ApplicationController,state,action,request,revision,operation string) {
	t.Helper();c.mu.Lock();defer c.mu.Unlock();p:=applicationPayload{Action:action,Revision:revision,Operation:operation,Confirm:true,Binding:c.binding};now:=applicationNow();code:="";if state=="uncertain"{code="uncertain"}
	next:=applicationCloneState(c.state);next.Jobs=append(next.Jobs,applicationRecord{Request:request,Payload:p,PayloadSHA256:applicationPayloadSHA(p),State:state,CreatedAt:now,UpdatedAt:now,ErrorCode:code});next.Latest=request;if c.writeState(next,false)!=nil{t.Fatal("synthetic private record could not be staged")}
}
func TestAdminV2ApplicationAbandonedQueuedAndRunningAreNotReplayed(t *testing.T) {
	for _,state:=range []string{"queued","running"}{t.Run(state,func(t *testing.T){m,c:=applicationTestController(t);request,revision,operation:=strings.Repeat("R",43),strings.Repeat("V",43),strings.Repeat("O",43);applicationTestStage(t,c,state,"apply",request,revision,operation);if c.Close()!=nil{t.Fatal("close failed")};next,e:=newV2ApplicationController(m,applicationTestOptions());if e!=nil{t.Fatal("reopen failed")};defer next.Close();m.application=next;var runs atomic.Int32;next.mutate=func([]string,*os.File)([]byte,error){runs.Add(1);return nil,errors.New("must not replay")};w:=applicationTestHTTP(t,m,"POST","/api/v2/application/apply",applicationTestBody("apply",request,revision,operation));value:=applicationTestResponse(t,w);if w.Code!=200||value.Job.State!="uncertain"||runs.Load()!=0{t.Fatal("abandoned request automatically replayed or claimed done")}})}
}
func TestAdminV2ApplicationLeaseBlocksSecondControllerButGetRemainsReadable(t *testing.T) {
	m,c:=applicationTestController(t);other,e:=newV2ApplicationController(m,applicationTestOptions());if e!=nil{t.Fatal("read-only second controller could not initialize")};defer other.Close();if other.owned{t.Fatal("exclusive lease was shared")};m.application=other;other.observe=func(context.Context,[]string)([]byte,error){v:=applicationTestOverview(nil);v.Busy=true;v.InstalledState="unconfirmed";return applicationTestEnvelope(v),nil}
	if w:=applicationTestHTTP(t,m,"GET","/api/v2/application","");w.Code!=200||!applicationTestResponse(t,w).Overview.Busy{t.Fatal("GET blocked behind a foreign lease")}
	body:=applicationTestBody("prepare",strings.Repeat("R",43),strings.Repeat("V",43),"");if w:=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body);w.Code!=409{t.Fatal("foreign lease admitted a mutation")}
	if c.Close()!=nil{t.Fatal("first close failed")};other.mutate=func([]string,*os.File)([]byte,error){return applicationTestEnvelope(applicationTestOperation("prepared",strings.Repeat("O",43),strings.Repeat("V",43))),nil};if w:=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",body);w.Code!=202{t.Fatal("released lease was not acquired")};applicationTestWait(t,other)
}
func TestAdminV2ApplicationResultMappingAndSafeUncertainErrors(t *testing.T) {
	for _,test:=range []struct{action,result,state,code string;invalid bool}{{"prepare","prepared","succeeded","",false},{"apply","applied","succeeded","",false},{"apply","rolled-back","failed","rolled-back",false},{"recover","rolled-back","succeeded","",false},{"apply","starting","uncertain","uncertain",false},{"apply","","uncertain","uncertain",true}}{t.Run(test.action+"-"+test.result,func(t *testing.T){m,c:=applicationTestController(t);request,revision,operation:=strings.Repeat("R",43),strings.Repeat("V",43),strings.Repeat("O",43);c.mutate=func([]string,*os.File)([]byte,error){if test.invalid{return []byte("private-child-diagnostic-must-not-leak"),errors.New("private-child-argv-must-not-leak")};return applicationTestEnvelope(applicationTestOperation(test.result,operation,revision)),nil};body:=applicationTestBody(test.action,request,revision,operation);if applicationTestHTTP(t,m,"POST","/api/v2/application/"+test.action,body).Code!=202{t.Fatal("enqueue failed")};applicationTestWait(t,c);w:=applicationTestHTTP(t,m,"POST","/api/v2/application/"+test.action,body);value:=applicationTestResponse(t,w);if value.Job.State!=test.state||value.Job.ErrorCode!=test.code||strings.Contains(w.Body.String(),"must-not-leak"){t.Fatal("child result incorrectly mapped/reflected")}})}
}
func TestAdminV2ApplicationReadOnlyReconcileRequiresExactOperationBinding(t *testing.T) {
	for _,matching:=range []bool{false,true}{t.Run(map[bool]string{false:"different-operation",true:"same-operation"}[matching],func(t *testing.T){m,c:=applicationTestController(t);request,revision,operation:=strings.Repeat("R",43),strings.Repeat("V",43),strings.Repeat("O",43);applicationTestStage(t,c,"uncertain","apply",request,revision,operation);selected:=operation;if !matching{selected=strings.Repeat("X",43)};c.observe=func(context.Context,[]string)([]byte,error){return applicationTestEnvelope(applicationTestOverview(applicationTestOperation("applied",selected,revision))),nil};w:=applicationTestHTTP(t,m,"GET","/api/v2/application?request="+request,"");value:=applicationTestResponse(t,w);wanted:="uncertain";if matching{wanted="succeeded"};if w.Code!=200||value.Job.State!=wanted{t.Fatal("journal reconciliation guessed another operation")}})}
}
func TestAdminV2ApplicationShutdownWaitDoesNotCancelWorkerOrInventFailure(t *testing.T) {
	m,c:=applicationTestController(t);started,release:=make(chan struct{}),make(chan struct{});var once sync.Once;defer func(){once.Do(func(){close(release)});applicationTestWait(t,c)}();revision:=strings.Repeat("V",43)
	c.mutate=func([]string,*os.File)([]byte,error){close(started);<-release;return applicationTestEnvelope(applicationTestOperation("prepared",strings.Repeat("O",43),revision)),nil}
	if applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",applicationTestBody("prepare",strings.Repeat("R",43),revision,"")).Code!=202{t.Fatal("enqueue failed")};select{case <-started:case <-time.After(2*time.Second):t.Fatal("worker did not start")}
	ctx,cancel:=context.WithCancel(context.Background());cancel();if !errors.Is(c.Shutdown(ctx),context.Canceled){t.Fatal("shutdown did not report its wait deadline")};c.mu.Lock();job,_:=c.selected("");c.mu.Unlock();if job.State!="running"{t.Fatal("wait cancellation falsely completed a job")};if c.Close()==nil{t.Fatal("active lease was prematurely released")};once.Do(func(){close(release)});applicationTestWait(t,c)
}
func TestAdminV2ApplicationPrivateStateRejectsPermissionsLinksAndCorruptSchema(t *testing.T) {
	for _,kind:=range []string{"mode","symlink","hardlink","duplicate","unknown","null-latest"}{t.Run(kind,func(t *testing.T){m,c:=applicationTestController(t);if c.Close()!=nil{t.Fatal("close failed")};path:=filepath.Join(m.base,".v2-http-jobs","state.json");switch kind{case "mode":if os.Chmod(path,0640)!=nil{t.Fatal("fixture chmod failed")};case "symlink":original:=path+".original";if os.Rename(path,original)!=nil||os.Symlink(original,path)!=nil{t.Fatal("owned symlink fixture failed")};case "hardlink":if os.Link(path,filepath.Join(t.TempDir(),"owned-link"))!=nil{t.Fatal("owned hardlink fixture failed")};case "duplicate":if os.WriteFile(path,[]byte(`{"version":1,"latest":"","jobs":[],"Version":1}`),0600)!=nil{t.Fatal("fixture write failed")};case "unknown":if os.WriteFile(path,[]byte(`{"version":1,"latest":"","jobs":[],"extra":false}`),0600)!=nil{t.Fatal("fixture write failed")};case "null-latest":if os.WriteFile(path,[]byte(`{"version":1,"latest":null,"jobs":[]}`),0600)!=nil{t.Fatal("fixture write failed")}};next,e:=newV2ApplicationController(m,applicationTestOptions());if e==nil||next!=nil||strings.Contains(e.Error(),m.base){t.Fatal("unsafe private state accepted/reflected")}})}
}
func TestAdminV2ApplicationExternalStateEditStopsBeforeMutation(t *testing.T) {
	m,c:=applicationTestController(t);var runs atomic.Int32;c.mutate=func([]string,*os.File)([]byte,error){runs.Add(1);return nil,errors.New("unexpected")};path:=filepath.Join(c.directory,"state.json");if e:=os.WriteFile(path,[]byte("{\"version\":1,\"latest\":\"\",\"jobs\":[]} \n"),0600);e!=nil{t.Fatal(e)}
	w:=applicationTestHTTP(t,m,"POST","/api/v2/application/prepare",applicationTestBody("prepare",strings.Repeat("R",43),strings.Repeat("V",43),""));if w.Code!=503||runs.Load()!=0{t.Fatal("external private state edit was overwritten or executed")}
}
func TestAdminV2ApplicationOverviewRejectsUnknownFieldsAndOptInEnvironmentIsStripped(t *testing.T) {
	m,c:=applicationTestController(t);c.observe=func(context.Context,[]string)([]byte,error){return []byte(`{"ok":true,"value":{"schemaVersion":1,"busy":false,"operation":null,"installed":null,"installedState":"unknown","statusSource":"journal","gatewayApplied":false,"firewallApplied":false,"secret":"must-not-leak"}}`),nil}
	w:=applicationTestHTTP(t,m,"GET","/api/v2/application","");if w.Code!=503||strings.Contains(w.Body.String(),"must-not-leak"){t.Fatal("unexpected helper fields escaped validation")}
	for _,name:=range []string{"VOWIN_OPT_IN","VOCAT_OPT_IN","PBX_ADMIN_OPT_IN","DOCKER_HOST","PYTHONPATH"}{t.Setenv(name,"synthetic-secret-must-not-leak")};for _,item:=range applicationEnvironment(){if strings.Contains(item,"synthetic-secret-must-not-leak"){t.Fatal("helper retained an opt-in/daemon/python environment override")}}
}
