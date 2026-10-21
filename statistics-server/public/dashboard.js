const $=id=>document.getElementById(id);
const labels={invalid_package:'Архив не прошёл проверку',network:'Сетевая ошибка',permission:'Недостаточно прав',timeout:'Тайм-аут',io:'Ошибка чтения или записи',operation:'Не удалось выполнить операцию',unexpected:'Неожиданная ошибка',engine_exit:'Движок завершился неожиданно'};
const stages={app_download:'Razret · скачивание',app_install:'Razret · установка',engine_download:'zapret · скачивание',engine_install:'zapret · установка',strategy_launch:'Запуск стратегии',strategy_test:'Проверка стратегии',diagnostic:'Диагностика',general:'Операция приложения'};
const services={discord:'Discord',youtube:'YouTube',launch:'Запуск'};
let snapshot,selectedTester;
const count=n=>new Intl.NumberFormat('ru-RU').format(n);
const percent=(passed,total)=>total?Math.round(passed/total*100)+'%':'—';
async function api(route,options={}){const res=await fetch('/api/'+route,{...options,headers:{'Content-Type':'application/json',...options.headers}});const data=await res.json();if(!res.ok){const e=new Error(data.error);e.status=res.status;throw e;}return data;}
function text(tag,value,className){const element=document.createElement(tag);element.textContent=value;if(className)element.className=className;return element;}
function row(values){const tr=document.createElement('tr');for(const value of values)tr.append(text('td',value));return tr;}
function cause(code){if(!code)return 'Техническая причина не передана';const known={timeout:'Превышено время ожидания','request:1':'Не удалось определить адрес сервиса (DNS)','request:2':'Не удалось установить соединение','request:3':'Не удалось установить защищённое соединение (TLS)','request:9':'Некорректный ответ сервера','request:10':'Сервер оборвал ответ','socket:10060':'Соединение не ответило вовремя','socket:10061':'Соединение отклонено','socket:11001':'Не удалось определить адрес (DNS)','win32:5':'Windows отказала в доступе','win32:32':'Файл занят другой программой','hresult:80070005':'Отказано в доступе','hresult:80070020':'Файл занят другой программой','hresult:80070002':'Файл не найден','response:invalid':'Ответ получен, но содержимое не прошло проверку','gateway:no_hello':'Discord не прислал ожидаемый ответ Gateway','read:failed':'Не удалось прочитать ответ','websocket:failed':'Ошибка WebSocket'};return known[code]??(code.startsWith('http:')?'Сервер вернул HTTP '+code.slice(5):code.startsWith('engine:')?'Движок завершился с кодом '+code.slice(7):'Технический код '+code);}
function showProblems(root,values){root.replaceChildren();if(!values.length){root.append(text('p','Проблем в отчётах нет.'));return;}for(const p of values){const block=text('div','', 'problem');const details=text('div',p.cause?cause(p.cause):labels[p.category]??'Техническая причина не передана');details.append(text('small',[stages[p.scope]??labels[p.category]??'Проверка',p.strategy?.replace('.bat',''),services[p.service]].filter(Boolean).join(' · ')));block.append(details,text('strong',count(p.count)));root.append(block);}}
function table(headers,rows){const wrap=text('div','', 'table-wrap'),t=document.createElement('table'),head=document.createElement('thead'),tr=document.createElement('tr');headers.forEach(h=>tr.append(text('th',h)));head.append(tr);const body=document.createElement('tbody');body.append(...rows.map(row));t.append(head,body);wrap.append(t);return wrap;}
function strategyRows(strategies){return strategies.map(s=>[s.name.replace('.bat',''),count(s.tests),percent(s.passed,s.tests),percent(s.discord.passed,s.discord.tests),percent(s.youtube.passed,s.youtube.tests)]);}
function versionItems(root,versions){root.replaceChildren(...versions.map(v=>{const r=text('div','', 'row');r.append(text('span',v.version),text('strong',count(v.reports)));return r;}));}
function confirmationView(t){
  const info=t.versionConfirmation??{versions:[],day:null,status:'awaiting'},target=snapshot.targetAppVersion??'0.7.11';
  const date=info.day?info.day.split('-').reverse().join('.'):null;
  return {...info,target,date,value:info.versions.join(' / ')||'—',label:info.status==='confirmed'?'Подтверждено '+target+'+':info.status==='mixed'?'Разные версии за день':'Нет новых данных'};
}
function testerList(){
  if(!snapshot)return;
  const query=$('tester-search').value.trim().toLowerCase();
  const list=snapshot.testers.filter(t=>(t.name+' '+t.code).toLowerCase().includes(query));
  $('version-reference').textContent='Обновление до '+(snapshot.targetAppVersion??'0.7.11')+' или новее подтверждается версией в отчёте. «Нет новых данных» не означает, что человек не обновился. Версии доступны за срок хранения отчётов, показатели качества — за 30 дней.';
  $('tester-list').replaceChildren();
  for(const t of list){
    const info=confirmationView(t),button=text('button','','tester-button');
    button.setAttribute('aria-pressed',String(t.id===selectedTester));
    const heading=text('span','','tester-heading'),name=text('span',t.name||t.code,'tester-name'),v=text('span',info.value,'tester-version');
    name.title=t.name||t.code;v.title='Версия Razret по последним отчётам';heading.append(name,v);
    const badge=text('span',info.label,'version-badge version-'+info.status);
    badge.title=info.date?'День отчёта (UTC): '+info.date+'. Это не время запуска приложения.':'Новых отчётов с версией приложения пока нет.';
    button.append(heading,badge,text('small',(t.name?t.code+' · ':'')+'Ошибки: '+count(t.errors)+' · Тесты: '+count(t.tests)));
    button.addEventListener('click',()=>{selectedTester=t.id;testerList();testerDetail();});
    $('tester-list').append(button);
  }
  if(!list.length)$('tester-list').append(text('p','Подходящих отчётов пока нет.'));
}
function testerDetail(){
  const t=snapshot.testers.find(t=>t.id===selectedTester),root=$('tester-detail');root.replaceChildren();
  if(!t){root.append(text('p','Выберите тестировщика.'));return;}
  root.append(text('h2',t.name||'Тестовая установка'),text('div',t.code,'code'));
  const info=confirmationView(t),versionCard=text('div','','confirmed-version');
  versionCard.append(text('span',info.versions.length>1?'Версии в последнем дне с отчётами':'Последняя подтверждённая версия','secondary'),text('strong','Razret '+info.value),text('span',info.label,'version-badge version-'+info.status));
  if(info.date){const date=text('small','Отчёт за '+info.date);date.title='День отчёта в UTC, без точного времени действий';versionCard.append(date);}
  if(info.status==='mixed')versionCard.append(text('p','За один день получены отчёты с разными версиями. Их порядок неизвестен.'+(info.targetConfirmed?' Есть подтверждение версии '+info.target+' или новее.':'')));
  else if(info.status==='awaiting')versionCard.append(text('p','Обновление до '+info.target+' пока не подтверждено новым отчётом. Пользователь мог уже обновиться.'));
  root.append(versionCard);
  const form=text('form','','alias-form'),input=document.createElement('input');
  input.value=t.name;input.maxLength=60;input.placeholder='Имя для этой панели';input.setAttribute('aria-label','Имя тестировщика');
  const save=text('button','Сохранить имя');save.type='submit';form.append(input,save);
  form.addEventListener('submit',async e=>{e.preventDefault();save.disabled=true;try{await api('tester-label',{method:'POST',body:JSON.stringify({TesterId:t.id,Name:input.value})});await refresh();}catch(error){$('message').textContent=error.message;save.disabled=false;}});
  root.append(form);const metrics=text('div','','detail-metrics');
  metrics.append(text('span','Тесты: '+count(t.tests)),text('strong',percent(t.passed,t.tests)+' успешных'),text('span','Ошибки: '+count(t.errors)));root.append(metrics,text('h3','Причины проблем'));
  const problems=text('div','');showProblems(problems,t.problems);root.append(problems);
  const details=document.createElement('details');details.append(text('summary','Результаты стратегий'),table(['Стратегия','Тестов','Успех','Discord','YouTube'],strategyRows(t.strategies)));root.append(details);
  const versions=document.createElement('details');versions.append(text('summary','Версии и обновления за 30 дней'));const list=text('div','');versionItems(list,t.versions);
  versions.append(list,table(['Этап','Версия','Успешно','Ошибки'],t.updates.map(u=>[stages[u.kind],u.version,count(u.success),count(u.failure)])));root.append(versions);
}
async function refresh(){$('refresh').disabled=true;$('message').textContent='';try{snapshot=await api('summary');$('login').hidden=true;$('dashboard').hidden=false;$('logout').hidden=false;for(const name of ['tests','errors'])$(name).textContent=count(snapshot[name]);$('passed').textContent=percent(snapshot.passed,snapshot.tests);$('tester-count').textContent=count(snapshot.testers.length);$('empty').hidden=snapshot.reports!==0;$('strategy-count').textContent=count(snapshot.strategies.length);$('strategies').replaceChildren(...strategyRows(snapshot.strategies).map(row));showProblems($('problems'),snapshot.problems.slice(0,5));if(snapshot.problems.length>5){const details=document.createElement('details');details.append(text('summary','Все причины ('+count(snapshot.problems.length)+')'));const list=text('div','');showProblems(list,snapshot.problems);details.append(list);$('problems').append(details);}versionItems($('versions'),snapshot.versions);$('updates').replaceChildren(...snapshot.updates.map(u=>row([stages[u.kind],u.version,count(u.success),count(u.failure)])));$('anonymous-note').textContent=snapshot.anonymousReports?'Отчёты старого формата без кода: '+count(snapshot.anonymousReports)+'. Они доступны только в общей сводке.':'';if(!selectedTester||!snapshot.testers.some(t=>t.id===selectedTester))selectedTester=snapshot.testers[0]?.id;testerList();testerDetail();if(snapshot.truncated)$('message').textContent='Сводка ограничена 5000 отчётами.';}catch(e){if(e.status===401){$('login').hidden=false;$('dashboard').hidden=true;$('logout').hidden=true;}else $('message').textContent=e.message??'Не удалось загрузить статистику.';}finally{$('refresh').disabled=false;}}
function tab(name){$('overview').hidden=name!=='overview';$('testers').hidden=name!=='testers';$('overview-tab').setAttribute('aria-pressed',String(name==='overview'));$('testers-tab').setAttribute('aria-pressed',String(name==='testers'));}
$('overview-tab').addEventListener('click',()=>tab('overview'));$('testers-tab').addEventListener('click',()=>tab('testers'));$('tester-search').addEventListener('input',testerList);
$('login-form').addEventListener('submit',async e=>{e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;try{await api('login',{method:'POST',body:JSON.stringify({password:$('password').value})});$('password').value='';await refresh();}catch(err){$('message').textContent=err.message;}finally{button.disabled=false;}});$('logout').addEventListener('click',async()=>{try{await api('logout',{method:'POST'});await refresh();}catch(e){$('message').textContent=e.message;}});$('refresh').addEventListener('click',refresh);refresh();
