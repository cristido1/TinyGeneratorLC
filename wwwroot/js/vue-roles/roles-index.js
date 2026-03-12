import{m as k,o as m,c as b,a as M,r as J,b as re,d as Vn,e as F,w as A,F as D,f as Z,g as $t,h as Ce,n as Y,i as P,t as j,T as Ot,j as L,k as Tt,l as y,p as Tn,u as g,q as Nn,s as An,v as Nt,x as En,y as z,z as $n}from"./chunks/primeicons-CDhzlABi.js";import{B as xe,s as On,f as zt,a as zn,b as Kn,R as Dn,c as Rn,d as Je,e as ie,z as Ue,M as At,Y as Bn,C as Un,D as _n,v as _e,x as je,S as jn,g as ee,l as Se,O as Gn,J as qn,m as Kt,h as Hn,i as Wn,j as R,u as Zn,k as q,n as Ge,o as Jn,p as qe,q as He,r as Et,t as Yn,P as Qn,Q as Xn,w as ei}from"./chunks/index-pttFU7Tr.js";var ti=`
    .p-textarea {
        font-family: inherit;
        font-feature-settings: inherit;
        font-size: 1rem;
        color: dt('textarea.color');
        background: dt('textarea.background');
        padding-block: dt('textarea.padding.y');
        padding-inline: dt('textarea.padding.x');
        border: 1px solid dt('textarea.border.color');
        transition:
            background dt('textarea.transition.duration'),
            color dt('textarea.transition.duration'),
            border-color dt('textarea.transition.duration'),
            outline-color dt('textarea.transition.duration'),
            box-shadow dt('textarea.transition.duration');
        appearance: none;
        border-radius: dt('textarea.border.radius');
        outline-color: transparent;
        box-shadow: dt('textarea.shadow');
    }

    .p-textarea:enabled:hover {
        border-color: dt('textarea.hover.border.color');
    }

    .p-textarea:enabled:focus {
        border-color: dt('textarea.focus.border.color');
        box-shadow: dt('textarea.focus.ring.shadow');
        outline: dt('textarea.focus.ring.width') dt('textarea.focus.ring.style') dt('textarea.focus.ring.color');
        outline-offset: dt('textarea.focus.ring.offset');
    }

    .p-textarea.p-invalid {
        border-color: dt('textarea.invalid.border.color');
    }

    .p-textarea.p-variant-filled {
        background: dt('textarea.filled.background');
    }

    .p-textarea.p-variant-filled:enabled:hover {
        background: dt('textarea.filled.hover.background');
    }

    .p-textarea.p-variant-filled:enabled:focus {
        background: dt('textarea.filled.focus.background');
    }

    .p-textarea:disabled {
        opacity: 1;
        background: dt('textarea.disabled.background');
        color: dt('textarea.disabled.color');
    }

    .p-textarea::placeholder {
        color: dt('textarea.placeholder.color');
    }

    .p-textarea.p-invalid::placeholder {
        color: dt('textarea.invalid.placeholder.color');
    }

    .p-textarea-fluid {
        width: 100%;
    }

    .p-textarea-resizable {
        overflow: hidden;
        resize: none;
    }

    .p-textarea-sm {
        font-size: dt('textarea.sm.font.size');
        padding-block: dt('textarea.sm.padding.y');
        padding-inline: dt('textarea.sm.padding.x');
    }

    .p-textarea-lg {
        font-size: dt('textarea.lg.font.size');
        padding-block: dt('textarea.lg.padding.y');
        padding-inline: dt('textarea.lg.padding.x');
    }
`,ni={root:function(e){var o=e.instance,u=e.props;return["p-textarea p-component",{"p-filled":o.$filled,"p-textarea-resizable ":u.autoResize,"p-textarea-sm p-inputfield-sm":u.size==="small","p-textarea-lg p-inputfield-lg":u.size==="large","p-invalid":o.$invalid,"p-variant-filled":o.$variant==="filled","p-textarea-fluid":o.$fluid}]}},ii=xe.extend({name:"textarea",style:ti,classes:ni}),ri={name:"BaseTextarea",extends:On,props:{autoResize:Boolean},style:ii,provide:function(){return{$pcTextarea:this,$parentInstance:this}}};function ce(r){"@babel/helpers - typeof";return ce=typeof Symbol=="function"&&typeof Symbol.iterator=="symbol"?function(e){return typeof e}:function(e){return e&&typeof Symbol=="function"&&e.constructor===Symbol&&e!==Symbol.prototype?"symbol":typeof e},ce(r)}function ai(r,e,o){return(e=oi(e))in r?Object.defineProperty(r,e,{value:o,enumerable:!0,configurable:!0,writable:!0}):r[e]=o,r}function oi(r){var e=li(r,"string");return ce(e)=="symbol"?e:e+""}function li(r,e){if(ce(r)!="object"||!r)return r;var o=r[Symbol.toPrimitive];if(o!==void 0){var u=o.call(r,e);if(ce(u)!="object")return u;throw new TypeError("@@toPrimitive must return a primitive value.")}return(e==="string"?String:Number)(r)}var We={name:"Textarea",extends:ri,inheritAttrs:!1,observer:null,mounted:function(){var e=this;this.autoResize&&(this.observer=new ResizeObserver(function(){requestAnimationFrame(function(){e.resize()})}),this.observer.observe(this.$el))},updated:function(){this.autoResize&&this.resize()},beforeUnmount:function(){this.observer&&this.observer.disconnect()},methods:{resize:function(){if(this.$el.offsetParent){var e=this.$el.style.height,o=parseInt(e)||0,u=this.$el.scrollHeight,h=!o||u>o,l=o&&u<o;l?(this.$el.style.height="auto",this.$el.style.height="".concat(this.$el.scrollHeight,"px")):h&&(this.$el.style.height="".concat(u,"px"))}},onInput:function(e){this.autoResize&&this.resize(),this.writeValue(e.target.value,e)}},computed:{attrs:function(){return k(this.ptmi("root",{context:{filled:this.$filled,disabled:this.disabled}}),this.formField)},dataP:function(){return zt(ai({invalid:this.$invalid,fluid:this.$fluid,filled:this.$variant==="filled"},this.size,this.size))}}},si=["value","name","disabled","aria-invalid","data-p"];function ui(r,e,o,u,h,l){return m(),b("textarea",k({class:r.cx("root"),value:r.d_value,name:r.name,disabled:r.disabled,"aria-invalid":r.invalid||void 0,"data-p":l.dataP,onInput:e[0]||(e[0]=function(){return l.onInput&&l.onInput.apply(l,arguments)})},l.attrs),null,16,si)}We.render=ui;var di=`
    .p-toggleswitch {
        display: inline-block;
        width: dt('toggleswitch.width');
        height: dt('toggleswitch.height');
    }

    .p-toggleswitch-input {
        cursor: pointer;
        appearance: none;
        position: absolute;
        top: 0;
        inset-inline-start: 0;
        width: 100%;
        height: 100%;
        padding: 0;
        margin: 0;
        opacity: 0;
        z-index: 1;
        outline: 0 none;
        border-radius: dt('toggleswitch.border.radius');
    }

    .p-toggleswitch-slider {
        cursor: pointer;
        width: 100%;
        height: 100%;
        border-width: dt('toggleswitch.border.width');
        border-style: solid;
        border-color: dt('toggleswitch.border.color');
        background: dt('toggleswitch.background');
        transition:
            background dt('toggleswitch.transition.duration'),
            color dt('toggleswitch.transition.duration'),
            border-color dt('toggleswitch.transition.duration'),
            outline-color dt('toggleswitch.transition.duration'),
            box-shadow dt('toggleswitch.transition.duration');
        border-radius: dt('toggleswitch.border.radius');
        outline-color: transparent;
        box-shadow: dt('toggleswitch.shadow');
    }

    .p-toggleswitch-handle {
        position: absolute;
        top: 50%;
        display: flex;
        justify-content: center;
        align-items: center;
        background: dt('toggleswitch.handle.background');
        color: dt('toggleswitch.handle.color');
        width: dt('toggleswitch.handle.size');
        height: dt('toggleswitch.handle.size');
        inset-inline-start: dt('toggleswitch.gap');
        margin-block-start: calc(-1 * calc(dt('toggleswitch.handle.size') / 2));
        border-radius: dt('toggleswitch.handle.border.radius');
        transition:
            background dt('toggleswitch.transition.duration'),
            color dt('toggleswitch.transition.duration'),
            inset-inline-start dt('toggleswitch.slide.duration'),
            box-shadow dt('toggleswitch.slide.duration');
    }

    .p-toggleswitch.p-toggleswitch-checked .p-toggleswitch-slider {
        background: dt('toggleswitch.checked.background');
        border-color: dt('toggleswitch.checked.border.color');
    }

    .p-toggleswitch.p-toggleswitch-checked .p-toggleswitch-handle {
        background: dt('toggleswitch.handle.checked.background');
        color: dt('toggleswitch.handle.checked.color');
        inset-inline-start: calc(dt('toggleswitch.width') - calc(dt('toggleswitch.handle.size') + dt('toggleswitch.gap')));
    }

    .p-toggleswitch:not(.p-disabled):has(.p-toggleswitch-input:hover) .p-toggleswitch-slider {
        background: dt('toggleswitch.hover.background');
        border-color: dt('toggleswitch.hover.border.color');
    }

    .p-toggleswitch:not(.p-disabled):has(.p-toggleswitch-input:hover) .p-toggleswitch-handle {
        background: dt('toggleswitch.handle.hover.background');
        color: dt('toggleswitch.handle.hover.color');
    }

    .p-toggleswitch:not(.p-disabled):has(.p-toggleswitch-input:hover).p-toggleswitch-checked .p-toggleswitch-slider {
        background: dt('toggleswitch.checked.hover.background');
        border-color: dt('toggleswitch.checked.hover.border.color');
    }

    .p-toggleswitch:not(.p-disabled):has(.p-toggleswitch-input:hover).p-toggleswitch-checked .p-toggleswitch-handle {
        background: dt('toggleswitch.handle.checked.hover.background');
        color: dt('toggleswitch.handle.checked.hover.color');
    }

    .p-toggleswitch:not(.p-disabled):has(.p-toggleswitch-input:focus-visible) .p-toggleswitch-slider {
        box-shadow: dt('toggleswitch.focus.ring.shadow');
        outline: dt('toggleswitch.focus.ring.width') dt('toggleswitch.focus.ring.style') dt('toggleswitch.focus.ring.color');
        outline-offset: dt('toggleswitch.focus.ring.offset');
    }

    .p-toggleswitch.p-invalid > .p-toggleswitch-slider {
        border-color: dt('toggleswitch.invalid.border.color');
    }

    .p-toggleswitch.p-disabled {
        opacity: 1;
    }

    .p-toggleswitch.p-disabled .p-toggleswitch-slider {
        background: dt('toggleswitch.disabled.background');
    }

    .p-toggleswitch.p-disabled .p-toggleswitch-handle {
        background: dt('toggleswitch.handle.disabled.background');
    }
`,ci={root:{position:"relative"}},mi={root:function(e){var o=e.instance,u=e.props;return["p-toggleswitch p-component",{"p-toggleswitch-checked":o.checked,"p-disabled":u.disabled,"p-invalid":o.$invalid}]},input:"p-toggleswitch-input",slider:"p-toggleswitch-slider",handle:"p-toggleswitch-handle"},fi=xe.extend({name:"toggleswitch",style:di,classes:mi,inlineStyles:ci}),pi={name:"BaseToggleSwitch",extends:zn,props:{trueValue:{type:null,default:!0},falseValue:{type:null,default:!1},readonly:{type:Boolean,default:!1},tabindex:{type:Number,default:null},inputId:{type:String,default:null},inputClass:{type:[String,Object],default:null},inputStyle:{type:Object,default:null},ariaLabelledby:{type:String,default:null},ariaLabel:{type:String,default:null}},style:fi,provide:function(){return{$pcToggleSwitch:this,$parentInstance:this}}},Dt={name:"ToggleSwitch",extends:pi,inheritAttrs:!1,emits:["change","focus","blur"],methods:{getPTOptions:function(e){var o=e==="root"?this.ptmi:this.ptm;return o(e,{context:{checked:this.checked,disabled:this.disabled}})},onChange:function(e){if(!this.disabled&&!this.readonly){var o=this.checked?this.falseValue:this.trueValue;this.writeValue(o,e),this.$emit("change",e)}},onFocus:function(e){this.$emit("focus",e)},onBlur:function(e){var o,u;this.$emit("blur",e),(o=(u=this.formField).onBlur)===null||o===void 0||o.call(u,e)}},computed:{checked:function(){return this.d_value===this.trueValue},dataP:function(){return zt({checked:this.checked,disabled:this.disabled,invalid:this.$invalid})}}},hi=["data-p-checked","data-p-disabled","data-p"],bi=["id","checked","tabindex","disabled","readonly","aria-checked","aria-labelledby","aria-label","aria-invalid"],gi=["data-p"],vi=["data-p"];function yi(r,e,o,u,h,l){return m(),b("div",k({class:r.cx("root"),style:r.sx("root")},l.getPTOptions("root"),{"data-p-checked":l.checked,"data-p-disabled":r.disabled,"data-p":l.dataP}),[M("input",k({id:r.inputId,type:"checkbox",role:"switch",class:[r.cx("input"),r.inputClass],style:r.inputStyle,checked:l.checked,tabindex:r.tabindex,disabled:r.disabled,readonly:r.readonly,"aria-checked":l.checked,"aria-labelledby":r.ariaLabelledby,"aria-label":r.ariaLabel,"aria-invalid":r.invalid||void 0,onFocus:e[0]||(e[0]=function(){return l.onFocus&&l.onFocus.apply(l,arguments)}),onBlur:e[1]||(e[1]=function(){return l.onBlur&&l.onBlur.apply(l,arguments)}),onChange:e[2]||(e[2]=function(){return l.onChange&&l.onChange.apply(l,arguments)})},l.getPTOptions("input")),null,16,bi),M("div",k({class:r.cx("slider")},l.getPTOptions("slider"),{"data-p":l.dataP}),[M("div",k({class:r.cx("handle")},l.getPTOptions("handle"),{"data-p":l.dataP}),[J(r.$slots,"handle",{checked:l.checked})],16,vi)],16,gi)],16,hi)}Dt.render=yi;var Ii=`
    .p-tieredmenu {
        background: dt('tieredmenu.background');
        color: dt('tieredmenu.color');
        border: 1px solid dt('tieredmenu.border.color');
        border-radius: dt('tieredmenu.border.radius');
        min-width: 12.5rem;
    }
    

    .p-tieredmenu-root-list,
    .p-tieredmenu-submenu {
        margin: 0;
        padding: dt('tieredmenu.list.padding');
        list-style: none;
        outline: 0 none;
        display: flex;
        flex-direction: column;
        gap: dt('tieredmenu.list.gap');
    }

    .p-tieredmenu-submenu {
        position: absolute;
        min-width: 100%;
        z-index: 1;
        background: dt('tieredmenu.background');
        color: dt('tieredmenu.color');
        border: 1px solid dt('tieredmenu.border.color');
        border-radius: dt('tieredmenu.border.radius');
        box-shadow: dt('tieredmenu.shadow');
    }

    .p-tieredmenu-item {
        position: relative;
    }

    .p-tieredmenu-item-content {
        transition:
            background dt('tieredmenu.transition.duration'),
            color dt('tieredmenu.transition.duration');
        border-radius: dt('tieredmenu.item.border.radius');
        color: dt('tieredmenu.item.color');
    }

    .p-tieredmenu-item-link {
        cursor: pointer;
        display: flex;
        align-items: center;
        text-decoration: none;
        overflow: hidden;
        position: relative;
        color: inherit;
        padding: dt('tieredmenu.item.padding');
        gap: dt('tieredmenu.item.gap');
        user-select: none;
        outline: 0 none;
    }

    .p-tieredmenu-item-label {
        line-height: 1;
    }

    .p-tieredmenu-item-icon {
        color: dt('tieredmenu.item.icon.color');
    }

    .p-tieredmenu-submenu-icon {
        color: dt('tieredmenu.submenu.icon.color');
        margin-left: auto;
        font-size: dt('tieredmenu.submenu.icon.size');
        width: dt('tieredmenu.submenu.icon.size');
        height: dt('tieredmenu.submenu.icon.size');
    }

    .p-tieredmenu-submenu-icon:dir(rtl) {
        margin-left: 0;
        margin-right: auto;
    }

    .p-tieredmenu-item.p-focus > .p-tieredmenu-item-content {
        color: dt('tieredmenu.item.focus.color');
        background: dt('tieredmenu.item.focus.background');
    }

    .p-tieredmenu-item.p-focus > .p-tieredmenu-item-content .p-tieredmenu-item-icon {
        color: dt('tieredmenu.item.icon.focus.color');
    }

    .p-tieredmenu-item.p-focus > .p-tieredmenu-item-content .p-tieredmenu-submenu-icon {
        color: dt('tieredmenu.submenu.icon.focus.color');
    }

    .p-tieredmenu-item:not(.p-disabled) > .p-tieredmenu-item-content:hover {
        color: dt('tieredmenu.item.focus.color');
        background: dt('tieredmenu.item.focus.background');
    }

    .p-tieredmenu-item:not(.p-disabled) > .p-tieredmenu-item-content:hover .p-tieredmenu-item-icon {
        color: dt('tieredmenu.item.icon.focus.color');
    }

    .p-tieredmenu-item:not(.p-disabled) > .p-tieredmenu-item-content:hover .p-tieredmenu-submenu-icon {
        color: dt('tieredmenu.submenu.icon.focus.color');
    }

    .p-tieredmenu-item-active > .p-tieredmenu-item-content {
        color: dt('tieredmenu.item.active.color');
        background: dt('tieredmenu.item.active.background');
    }

    .p-tieredmenu-item-active > .p-tieredmenu-item-content .p-tieredmenu-item-icon {
        color: dt('tieredmenu.item.icon.active.color');
    }

    .p-tieredmenu-item-active > .p-tieredmenu-item-content .p-tieredmenu-submenu-icon {
        color: dt('tieredmenu.submenu.icon.active.color');
    }

    .p-tieredmenu-separator {
        border-block-start: 1px solid dt('tieredmenu.separator.border.color');
    }

    .p-tieredmenu-overlay {
        box-shadow: dt('tieredmenu.shadow');
        will-change: transform;
    }

    .p-tieredmenu-mobile .p-tieredmenu-submenu {
        position: static;
        box-shadow: none;
        border: 0 none;
        padding-inline-start: dt('tieredmenu.submenu.mobile.indent');
        padding-inline-end: 0;
    }

    .p-tieredmenu-mobile .p-tieredmenu-submenu:dir(rtl) {
        padding-inline-start: 0;
        padding-inline-end: dt('tieredmenu.submenu.mobile.indent');
    }

    .p-tieredmenu-mobile .p-tieredmenu-submenu-icon {
        transition: transform 0.2s;
        transform: rotate(90deg);
    }

    .p-tieredmenu-mobile .p-tieredmenu-item-active > .p-tieredmenu-item-content .p-tieredmenu-submenu-icon {
        transform: rotate(-90deg);
    }
`,wi={submenu:function(e){var o=e.instance,u=e.processedItem;return{display:o.isItemActive(u)?"flex":"none"}}},ki={root:function(e){var o=e.props,u=e.instance;return["p-tieredmenu p-component",{"p-tieredmenu-overlay":o.popup,"p-tieredmenu-mobile":u.queryMatches}]},start:"p-tieredmenu-start",rootList:"p-tieredmenu-root-list",item:function(e){var o=e.instance,u=e.processedItem;return["p-tieredmenu-item",{"p-tieredmenu-item-active":o.isItemActive(u),"p-focus":o.isItemFocused(u),"p-disabled":o.isItemDisabled(u)}]},itemContent:"p-tieredmenu-item-content",itemLink:"p-tieredmenu-item-link",itemIcon:"p-tieredmenu-item-icon",itemLabel:"p-tieredmenu-item-label",submenuIcon:"p-tieredmenu-submenu-icon",submenu:"p-tieredmenu-submenu",separator:"p-tieredmenu-separator",end:"p-tieredmenu-end"},Ci=xe.extend({name:"tieredmenu",style:Ii,classes:ki,inlineStyles:wi}),Si={name:"BaseTieredMenu",extends:Je,props:{popup:{type:Boolean,default:!1},model:{type:Array,default:null},appendTo:{type:[String,Object],default:"body"},breakpoint:{type:String,default:"960px"},autoZIndex:{type:Boolean,default:!0},baseZIndex:{type:Number,default:0},disabled:{type:Boolean,default:!1},tabindex:{type:Number,default:0},ariaLabelledby:{type:String,default:null},ariaLabel:{type:String,default:null}},style:Ci,provide:function(){return{$pcTieredMenu:this,$parentInstance:this}}},Rt={name:"TieredMenuSub",hostName:"TieredMenu",extends:Je,emits:["item-click","item-mouseenter","item-mousemove"],container:null,props:{menuId:{type:String,default:null},focusedItemId:{type:String,default:null},items:{type:Array,default:null},visible:{type:Boolean,default:!1},level:{type:Number,default:0},templates:{type:Object,default:null},activeItemPath:{type:Object,default:null},tabindex:{type:Number,default:0}},methods:{getItemId:function(e){return"".concat(this.menuId,"_").concat(e.key)},getItemKey:function(e){return this.getItemId(e)},getItemProp:function(e,o,u){return e&&e.item?Kt(e.item[o],u):void 0},getItemLabel:function(e){return this.getItemProp(e,"label")},getItemLabelId:function(e){return"".concat(this.menuId,"_").concat(e.key,"_label")},getPTOptions:function(e,o,u){return this.ptm(u,{context:{item:e.item,index:o,active:this.isItemActive(e),focused:this.isItemFocused(e),disabled:this.isItemDisabled(e)}})},isItemActive:function(e){return this.activeItemPath.some(function(o){return o.key===e.key})},isItemVisible:function(e){return this.getItemProp(e,"visible")!==!1},isItemDisabled:function(e){return this.getItemProp(e,"disabled")},isItemFocused:function(e){return this.focusedItemId===this.getItemId(e)},isItemGroup:function(e){return ie(e.items)},onEnter:function(){Hn(this.container,this.level)},onItemClick:function(e,o){this.getItemProp(o,"command",{originalEvent:e,item:o.item}),this.$emit("item-click",{originalEvent:e,processedItem:o,isFocus:!0})},onItemMouseEnter:function(e,o){this.$emit("item-mouseenter",{originalEvent:e,processedItem:o})},onItemMouseMove:function(e,o){this.$emit("item-mousemove",{originalEvent:e,processedItem:o})},getAriaSetSize:function(){var e=this;return this.items.filter(function(o){return e.isItemVisible(o)&&!e.getItemProp(o,"separator")}).length},getAriaPosInset:function(e){var o=this;return e-this.items.slice(0,e).filter(function(u){return o.isItemVisible(u)&&o.getItemProp(u,"separator")}).length+1},getMenuItemProps:function(e,o){return{action:k({class:this.cx("itemLink"),tabindex:-1},this.getPTOptions(e,o,"itemLink")),icon:k({class:[this.cx("itemIcon"),this.getItemProp(e,"icon")]},this.getPTOptions(e,o,"itemIcon")),label:k({class:this.cx("itemLabel")},this.getPTOptions(e,o,"itemLabel")),submenuicon:k({class:this.cx("submenuIcon")},this.getPTOptions(e,o,"submenuIcon"))}},containerRef:function(e){this.container=e}},components:{AngleRightIcon:Rn},directives:{ripple:Dn}},xi=["tabindex"],Li=["id","aria-label","aria-disabled","aria-expanded","aria-haspopup","aria-level","aria-setsize","aria-posinset","data-p-active","data-p-focused","data-p-disabled"],Fi=["onClick","onMouseenter","onMousemove"],Mi=["href","target"],Pi=["id"],Vi=["id"];function Ti(r,e,o,u,h,l){var C=re("AngleRightIcon"),$=re("TieredMenuSub",!0),w=Vn("ripple");return m(),F(Ot,k({name:"p-anchored-overlay",onEnter:l.onEnter},r.ptm("menu.transition")),{default:A(function(){return[o.level===0||o.visible?(m(),b("ul",{key:0,ref:l.containerRef,tabindex:o.tabindex},[(m(!0),b(D,null,Z(o.items,function(c,x){return m(),b(D,{key:l.getItemKey(c)},[l.isItemVisible(c)&&!l.getItemProp(c,"separator")?(m(),b("li",k({key:0,id:l.getItemId(c),style:l.getItemProp(c,"style"),class:[r.cx("item",{processedItem:c}),l.getItemProp(c,"class")],role:"menuitem","aria-label":l.getItemLabel(c),"aria-disabled":l.isItemDisabled(c)||void 0,"aria-expanded":l.isItemGroup(c)?l.isItemActive(c):void 0,"aria-haspopup":l.isItemGroup(c)&&!l.getItemProp(c,"to")?"menu":void 0,"aria-level":o.level+1,"aria-setsize":l.getAriaSetSize(),"aria-posinset":l.getAriaPosInset(x)},{ref_for:!0},l.getPTOptions(c,x,"item"),{"data-p-active":l.isItemActive(c),"data-p-focused":l.isItemFocused(c),"data-p-disabled":l.isItemDisabled(c)}),[M("div",k({class:r.cx("itemContent"),onClick:function(T){return l.onItemClick(T,c)},onMouseenter:function(T){return l.onItemMouseEnter(T,c)},onMousemove:function(T){return l.onItemMouseMove(T,c)}},{ref_for:!0},l.getPTOptions(c,x,"itemContent")),[o.templates.item?(m(),F(Ce(o.templates.item),{key:1,item:c.item,hasSubmenu:l.getItemProp(c,"items"),label:l.getItemLabel(c),props:l.getMenuItemProps(c,x)},null,8,["item","hasSubmenu","label","props"])):$t((m(),b("a",k({key:0,href:l.getItemProp(c,"url"),class:r.cx("itemLink"),target:l.getItemProp(c,"target"),tabindex:"-1"},{ref_for:!0},l.getPTOptions(c,x,"itemLink")),[o.templates.itemicon?(m(),F(Ce(o.templates.itemicon),{key:0,item:c.item,class:Y(r.cx("itemIcon"))},null,8,["item","class"])):l.getItemProp(c,"icon")?(m(),b("span",k({key:1,class:[r.cx("itemIcon"),l.getItemProp(c,"icon")]},{ref_for:!0},l.getPTOptions(c,x,"itemIcon")),null,16)):P("",!0),M("span",k({id:l.getItemLabelId(c),class:r.cx("itemLabel")},{ref_for:!0},l.getPTOptions(c,x,"itemLabel")),j(l.getItemLabel(c)),17,Pi),l.getItemProp(c,"items")?(m(),b(D,{key:2},[o.templates.submenuicon?(m(),F(Ce(o.templates.submenuicon),k({key:0,class:r.cx("submenuIcon"),active:l.isItemActive(c)},{ref_for:!0},l.getPTOptions(c,x,"submenuIcon")),null,16,["class","active"])):(m(),F(C,k({key:1,class:r.cx("submenuIcon")},{ref_for:!0},l.getPTOptions(c,x,"submenuIcon")),null,16,["class"]))],64)):P("",!0)],16,Mi)),[[w]])],16,Fi),l.isItemVisible(c)&&l.isItemGroup(c)?(m(),F($,k({key:0,id:l.getItemId(c)+"_list",class:r.cx("submenu"),style:r.sx("submenu",!0,{processedItem:c}),"aria-labelledby":l.getItemLabelId(c),role:"menu",menuId:o.menuId,focusedItemId:o.focusedItemId,items:c.items,templates:o.templates,activeItemPath:o.activeItemPath,level:o.level+1,visible:l.isItemActive(c)&&l.isItemGroup(c),pt:r.pt,unstyled:r.unstyled,onItemClick:e[0]||(e[0]=function(V){return r.$emit("item-click",V)}),onItemMouseenter:e[1]||(e[1]=function(V){return r.$emit("item-mouseenter",V)}),onItemMousemove:e[2]||(e[2]=function(V){return r.$emit("item-mousemove",V)})},{ref_for:!0},r.ptm("submenu")),null,16,["id","class","style","aria-labelledby","menuId","focusedItemId","items","templates","activeItemPath","level","visible","pt","unstyled"])):P("",!0)],16,Li)):P("",!0),l.isItemVisible(c)&&l.getItemProp(c,"separator")?(m(),b("li",k({key:1,id:l.getItemId(c),style:l.getItemProp(c,"style"),class:[r.cx("separator"),l.getItemProp(c,"class")],role:"separator"},{ref_for:!0},r.ptm("separator")),null,16,Vi)):P("",!0)],64)}),128))],8,xi)):P("",!0)]}),_:1},16,["onEnter"])}Rt.render=Ti;var Bt={name:"TieredMenu",extends:Si,inheritAttrs:!1,emits:["focus","blur","before-show","before-hide","hide","show"],outsideClickListener:null,matchMediaListener:null,scrollHandler:null,resizeListener:null,target:null,container:null,menubar:null,searchTimeout:null,searchValue:null,data:function(){return{focused:!1,focusedItemInfo:{index:-1,level:0,parentKey:""},activeItemPath:[],visible:!this.popup,submenuVisible:!1,dirty:!1,query:null,queryMatches:!1}},watch:{activeItemPath:function(e){this.popup||(ie(e)?(this.bindOutsideClickListener(),this.bindResizeListener()):(this.unbindOutsideClickListener(),this.unbindResizeListener()))}},mounted:function(){this.bindMatchMediaListener()},beforeUnmount:function(){this.unbindOutsideClickListener(),this.unbindResizeListener(),this.unbindMatchMediaListener(),this.scrollHandler&&(this.scrollHandler.destroy(),this.scrollHandler=null),this.container&&this.autoZIndex&&je.clear(this.container),this.target=null,this.container=null},methods:{getItemProp:function(e,o){return e?Kt(e[o]):void 0},getItemLabel:function(e){return this.getItemProp(e,"label")},isItemDisabled:function(e){return this.getItemProp(e,"disabled")},isItemVisible:function(e){return this.getItemProp(e,"visible")!==!1},isItemGroup:function(e){return ie(this.getItemProp(e,"items"))},isItemSeparator:function(e){return this.getItemProp(e,"separator")},getProccessedItemLabel:function(e){return e?this.getItemLabel(e.item):void 0},isProccessedItemGroup:function(e){return e&&ie(e.items)},toggle:function(e){this.visible?this.hide(e,!0):this.show(e)},show:function(e,o){this.popup&&(this.$emit("before-show"),this.visible=!0,this.target=this.target||e.currentTarget,this.relatedTarget=e.relatedTarget||null),o&&ee(this.menubar)},hide:function(e,o){this.popup&&(this.$emit("before-hide"),this.visible=!1),this.activeItemPath=[],this.focusedItemInfo={index:-1,level:0,parentKey:""},o&&ee(this.relatedTarget||this.target||this.menubar),this.dirty=!1},onFocus:function(e){this.focused=!0,this.popup||(this.focusedItemInfo=this.focusedItemInfo.index!==-1?this.focusedItemInfo:{index:this.findFirstFocusedItemIndex(),level:0,parentKey:""}),this.$emit("focus",e)},onBlur:function(e){this.focused=!1,this.focusedItemInfo={index:-1,level:0,parentKey:""},this.searchValue="",this.dirty=!1,this.$emit("blur",e)},onKeyDown:function(e){if(this.disabled){e.preventDefault();return}var o=e.metaKey||e.ctrlKey;switch(e.code){case"ArrowDown":this.onArrowDownKey(e);break;case"ArrowUp":this.onArrowUpKey(e);break;case"ArrowLeft":this.onArrowLeftKey(e);break;case"ArrowRight":this.onArrowRightKey(e);break;case"Home":this.onHomeKey(e);break;case"End":this.onEndKey(e);break;case"Space":this.onSpaceKey(e);break;case"Enter":case"NumpadEnter":this.onEnterKey(e);break;case"Escape":this.onEscapeKey(e);break;case"Tab":this.onTabKey(e);break;case"PageDown":case"PageUp":case"Backspace":case"ShiftLeft":case"ShiftRight":break;default:!o&&qn(e.key)&&this.searchItems(e,e.key);break}},onItemChange:function(e,o){var u=e.processedItem,h=e.isFocus;if(!Se(u)){var l=u.index,C=u.key,$=u.level,w=u.parentKey,c=u.items,x=ie(c),V=this.activeItemPath.filter(function(T){return T.parentKey!==w&&T.parentKey!==C});x&&(V.push(u),this.submenuVisible=!0),this.focusedItemInfo={index:l,level:$,parentKey:w},x&&(this.dirty=!0),h&&ee(this.menubar),!(o==="hover"&&this.queryMatches)&&(this.activeItemPath=V)}},onOverlayClick:function(e){Gn.emit("overlay-click",{originalEvent:e,target:this.target})},onItemClick:function(e){var o=e.originalEvent,u=e.processedItem,h=this.isProccessedItemGroup(u),l=Se(u.parent),C=this.isSelected(u);if(C){var $=u.index,w=u.key,c=u.level,x=u.parentKey;this.activeItemPath=this.activeItemPath.filter(function(T){return w!==T.key&&w.startsWith(T.key)}),this.focusedItemInfo={index:$,level:c,parentKey:x},this.dirty=!l,ee(this.menubar)}else if(h)this.onItemChange(e);else{var V=l?u:this.activeItemPath.find(function(T){return T.parentKey===""});this.hide(o),this.changeFocusedItemIndex(o,V?V.index:-1),ee(this.menubar)}},onItemMouseEnter:function(e){this.dirty&&this.onItemChange(e,"hover")},onItemMouseMove:function(e){this.focused&&this.changeFocusedItemIndex(e,e.processedItem.index)},onArrowDownKey:function(e){var o=this.focusedItemInfo.index!==-1?this.findNextItemIndex(this.focusedItemInfo.index):this.findFirstFocusedItemIndex();this.changeFocusedItemIndex(e,o),e.preventDefault()},onArrowUpKey:function(e){if(e.altKey){if(this.focusedItemInfo.index!==-1){var o=this.visibleItems[this.focusedItemInfo.index],u=this.isProccessedItemGroup(o);!u&&this.onItemChange({originalEvent:e,processedItem:o})}this.popup&&this.hide(e,!0),e.preventDefault()}else{var h=this.focusedItemInfo.index!==-1?this.findPrevItemIndex(this.focusedItemInfo.index):this.findLastFocusedItemIndex();this.changeFocusedItemIndex(e,h),e.preventDefault()}},onArrowLeftKey:function(e){var o=this,u=this.visibleItems[this.focusedItemInfo.index],h=this.activeItemPath.find(function(C){return C.key===u.parentKey}),l=Se(u.parent);l||(this.focusedItemInfo={index:-1,parentKey:h?h.parentKey:""},this.searchValue="",this.onArrowDownKey(e)),this.activeItemPath=this.activeItemPath.filter(function(C){return C.parentKey!==o.focusedItemInfo.parentKey}),e.preventDefault()},onArrowRightKey:function(e){var o=this.visibleItems[this.focusedItemInfo.index],u=this.isProccessedItemGroup(o);u&&(this.onItemChange({originalEvent:e,processedItem:o}),this.focusedItemInfo={index:-1,parentKey:o.key},this.searchValue="",this.onArrowDownKey(e)),e.preventDefault()},onHomeKey:function(e){this.changeFocusedItemIndex(e,this.findFirstItemIndex()),e.preventDefault()},onEndKey:function(e){this.changeFocusedItemIndex(e,this.findLastItemIndex()),e.preventDefault()},onEnterKey:function(e){if(this.focusedItemInfo.index!==-1){var o=Ue(this.menubar,'li[id="'.concat("".concat(this.focusedItemId),'"]')),u=o&&Ue(o,'[data-pc-section="itemlink"]');if(u?u.click():o&&o.click(),!this.popup){var h=this.visibleItems[this.focusedItemInfo.index],l=this.isProccessedItemGroup(h);!l&&(this.focusedItemInfo.index=this.findFirstFocusedItemIndex())}}e.preventDefault()},onSpaceKey:function(e){this.onEnterKey(e)},onEscapeKey:function(e){if(this.popup||this.focusedItemInfo.level!==0){var o=this.focusedItemInfo;this.hide(e,!1),this.focusedItemInfo={index:Number(o.parentKey.split("_")[0]),level:0,parentKey:""},this.popup&&ee(this.target)}e.preventDefault()},onTabKey:function(e){if(this.focusedItemInfo.index!==-1){var o=this.visibleItems[this.focusedItemInfo.index],u=this.isProccessedItemGroup(o);!u&&this.onItemChange({originalEvent:e,processedItem:o})}this.hide()},onEnter:function(e){this.autoZIndex&&je.set("menu",e,this.baseZIndex+this.$primevue.config.zIndex.menu),jn(e,{position:"absolute",top:"0"}),this.alignOverlay(),ee(this.menubar),this.scrollInView()},onAfterEnter:function(){this.bindOutsideClickListener(),this.bindScrollListener(),this.bindResizeListener(),this.$emit("show")},onLeave:function(){this.unbindOutsideClickListener(),this.unbindScrollListener(),this.unbindResizeListener(),this.$emit("hide"),this.container=null,this.dirty=!1},onAfterLeave:function(e){this.autoZIndex&&je.clear(e)},alignOverlay:function(){_n(this.container,this.target);var e=_e(this.target);e>_e(this.container)&&(this.container.style.minWidth=_e(this.target)+"px")},bindOutsideClickListener:function(){var e=this;this.outsideClickListener||(this.outsideClickListener=function(o){var u=e.container&&!e.container.contains(o.target),h=e.popup?!(e.target&&(e.target===o.target||e.target.contains(o.target))):!0;u&&h&&e.hide()},document.addEventListener("click",this.outsideClickListener,!0))},unbindOutsideClickListener:function(){this.outsideClickListener&&(document.removeEventListener("click",this.outsideClickListener,!0),this.outsideClickListener=null)},bindScrollListener:function(){var e=this;this.scrollHandler||(this.scrollHandler=new Un(this.target,function(o){e.hide(o,!0)})),this.scrollHandler.bindScrollListener()},unbindScrollListener:function(){this.scrollHandler&&this.scrollHandler.unbindScrollListener()},bindResizeListener:function(){var e=this;this.resizeListener||(this.resizeListener=function(o){Bn()||e.hide(o,!0)},window.addEventListener("resize",this.resizeListener))},unbindResizeListener:function(){this.resizeListener&&(window.removeEventListener("resize",this.resizeListener),this.resizeListener=null)},bindMatchMediaListener:function(){var e=this;if(!this.matchMediaListener){var o=matchMedia("(max-width: ".concat(this.breakpoint,")"));this.query=o,this.queryMatches=o.matches,this.matchMediaListener=function(){e.queryMatches=o.matches},this.query.addEventListener("change",this.matchMediaListener)}},unbindMatchMediaListener:function(){this.matchMediaListener&&(this.query.removeEventListener("change",this.matchMediaListener),this.matchMediaListener=null)},isItemMatched:function(e){var o;return this.isValidItem(e)&&((o=this.getProccessedItemLabel(e))===null||o===void 0?void 0:o.toLocaleLowerCase().startsWith(this.searchValue.toLocaleLowerCase()))},isValidItem:function(e){return!!e&&!this.isItemDisabled(e.item)&&!this.isItemSeparator(e.item)&&this.isItemVisible(e.item)},isValidSelectedItem:function(e){return this.isValidItem(e)&&this.isSelected(e)},isSelected:function(e){return this.activeItemPath.some(function(o){return o.key===e.key})},findFirstItemIndex:function(){var e=this;return this.visibleItems.findIndex(function(o){return e.isValidItem(o)})},findLastItemIndex:function(){var e=this;return At(this.visibleItems,function(o){return e.isValidItem(o)})},findNextItemIndex:function(e){var o=this,u=e<this.visibleItems.length-1?this.visibleItems.slice(e+1).findIndex(function(h){return o.isValidItem(h)}):-1;return u>-1?u+e+1:e},findPrevItemIndex:function(e){var o=this,u=e>0?At(this.visibleItems.slice(0,e),function(h){return o.isValidItem(h)}):-1;return u>-1?u:e},findSelectedItemIndex:function(){var e=this;return this.visibleItems.findIndex(function(o){return e.isValidSelectedItem(o)})},findFirstFocusedItemIndex:function(){var e=this.findSelectedItemIndex();return e<0?this.findFirstItemIndex():e},findLastFocusedItemIndex:function(){var e=this.findSelectedItemIndex();return e<0?this.findLastItemIndex():e},searchItems:function(e,o){var u=this;this.searchValue=(this.searchValue||"")+o;var h=-1,l=!1;return this.focusedItemInfo.index!==-1?(h=this.visibleItems.slice(this.focusedItemInfo.index).findIndex(function(C){return u.isItemMatched(C)}),h=h===-1?this.visibleItems.slice(0,this.focusedItemInfo.index).findIndex(function(C){return u.isItemMatched(C)}):h+this.focusedItemInfo.index):h=this.visibleItems.findIndex(function(C){return u.isItemMatched(C)}),h!==-1&&(l=!0),h===-1&&this.focusedItemInfo.index===-1&&(h=this.findFirstFocusedItemIndex()),h!==-1&&this.changeFocusedItemIndex(e,h),this.searchTimeout&&clearTimeout(this.searchTimeout),this.searchTimeout=setTimeout(function(){u.searchValue="",u.searchTimeout=null},500),l},changeFocusedItemIndex:function(e,o){this.focusedItemInfo.index!==o&&(this.focusedItemInfo.index=o,this.scrollInView())},scrollInView:function(){var e=arguments.length>0&&arguments[0]!==void 0?arguments[0]:-1,o=e!==-1?"".concat(this.$id,"_").concat(e):this.focusedItemId,u=Ue(this.menubar,'li[id="'.concat(o,'"]'));u&&u.scrollIntoView&&u.scrollIntoView({block:"nearest",inline:"start"})},createProcessedItems:function(e){var o=this,u=arguments.length>1&&arguments[1]!==void 0?arguments[1]:0,h=arguments.length>2&&arguments[2]!==void 0?arguments[2]:{},l=arguments.length>3&&arguments[3]!==void 0?arguments[3]:"",C=[];return e&&e.forEach(function($,w){var c=(l!==""?l+"_":"")+w,x={item:$,index:w,level:u,key:c,parent:h,parentKey:l};x.items=o.createProcessedItems($.items,u+1,x,c),C.push(x)}),C},containerRef:function(e){this.container=e},menubarRef:function(e){this.menubar=e?e.$el:void 0}},computed:{processedItems:function(){return this.createProcessedItems(this.model||[])},visibleItems:function(){var e=this,o=this.activeItemPath.find(function(u){return u.key===e.focusedItemInfo.parentKey});return o?o.items:this.processedItems},focusedItemId:function(){return this.focusedItemInfo.index!==-1?"".concat(this.$id).concat(ie(this.focusedItemInfo.parentKey)?"_"+this.focusedItemInfo.parentKey:"","_").concat(this.focusedItemInfo.index):null}},components:{TieredMenuSub:Rt,Portal:Kn}},Ni=["id"];function Ai(r,e,o,u,h,l){var C=re("TieredMenuSub"),$=re("Portal");return m(),F($,{appendTo:r.appendTo,disabled:!r.popup},{default:A(function(){return[L(Ot,k({name:"p-anchored-overlay",onEnter:l.onEnter,onAfterEnter:l.onAfterEnter,onLeave:l.onLeave,onAfterLeave:l.onAfterLeave},r.ptm("transition")),{default:A(function(){return[h.visible?(m(),b("div",k({key:0,ref:l.containerRef,id:r.$id,class:r.cx("root"),onClick:e[0]||(e[0]=function(){return l.onOverlayClick&&l.onOverlayClick.apply(l,arguments)})},r.ptmi("root")),[r.$slots.start?(m(),b("div",k({key:0,class:r.cx("start")},r.ptm("start")),[J(r.$slots,"start")],16)):P("",!0),L(C,k({ref:l.menubarRef,id:r.$id+"_list",class:r.cx("rootList"),tabindex:r.disabled?-1:r.tabindex,role:"menubar","aria-label":r.ariaLabel,"aria-labelledby":r.ariaLabelledby,"aria-disabled":r.disabled||void 0,"aria-orientation":"vertical","aria-activedescendant":h.focused?l.focusedItemId:void 0,menuId:r.$id,focusedItemId:h.focused?l.focusedItemId:void 0,items:l.processedItems,templates:r.$slots,activeItemPath:h.activeItemPath,level:0,visible:h.submenuVisible,pt:r.pt,unstyled:r.unstyled,onFocus:l.onFocus,onBlur:l.onBlur,onKeydown:l.onKeyDown,onItemClick:l.onItemClick,onItemMouseenter:l.onItemMouseEnter,onItemMousemove:l.onItemMouseMove},r.ptm("rootList")),null,16,["id","class","tabindex","aria-label","aria-labelledby","aria-disabled","aria-activedescendant","menuId","focusedItemId","items","templates","activeItemPath","visible","pt","unstyled","onFocus","onBlur","onKeydown","onItemClick","onItemMouseenter","onItemMousemove"]),r.$slots.end?(m(),b("div",k({key:1,class:r.cx("end")},r.ptm("end")),[J(r.$slots,"end")],16)):P("",!0)],16,Ni)):P("",!0)]}),_:3},16,["onEnter","onAfterEnter","onLeave","onAfterLeave"])]}),_:3},8,["appendTo","disabled"])}Bt.render=Ai;var Ei=`
    .p-splitbutton {
        display: inline-flex;
        position: relative;
        border-radius: dt('splitbutton.border.radius');
    }

    .p-splitbutton-button.p-button {
        border-start-end-radius: 0;
        border-end-end-radius: 0;
        border-inline-end: 0 none;
    }

    .p-splitbutton-button.p-button:focus-visible,
    .p-splitbutton-dropdown.p-button:focus-visible {
        z-index: 1;
    }

    .p-splitbutton-button.p-button:not(:disabled):hover,
    .p-splitbutton-button.p-button:not(:disabled):active {
        border-inline-end: 0 none;
    }

    .p-splitbutton-dropdown.p-button {
        border-start-start-radius: 0;
        border-end-start-radius: 0;
    }

    .p-splitbutton .p-menu {
        min-width: 100%;
    }

    .p-splitbutton-fluid {
        display: flex;
    }

    .p-splitbutton-rounded .p-splitbutton-dropdown.p-button {
        border-start-end-radius: dt('splitbutton.rounded.border.radius');
        border-end-end-radius: dt('splitbutton.rounded.border.radius');
    }

    .p-splitbutton-rounded .p-splitbutton-button.p-button {
        border-start-start-radius: dt('splitbutton.rounded.border.radius');
        border-end-start-radius: dt('splitbutton.rounded.border.radius');
    }

    .p-splitbutton-raised {
        box-shadow: dt('splitbutton.raised.shadow');
    }
`,$i={root:function(e){var o=e.instance,u=e.props;return["p-splitbutton p-component",{"p-splitbutton-raised":u.raised,"p-splitbutton-rounded":u.rounded,"p-splitbutton-fluid":o.hasFluid}]},pcButton:"p-splitbutton-button",pcDropdown:"p-splitbutton-dropdown"},Oi=xe.extend({name:"splitbutton",style:Ei,classes:$i}),zi={name:"BaseSplitButton",extends:Je,props:{label:{type:String,default:null},icon:{type:String,default:null},model:{type:Array,default:null},autoZIndex:{type:Boolean,default:!0},baseZIndex:{type:Number,default:0},appendTo:{type:[String,Object],default:"body"},disabled:{type:Boolean,default:!1},fluid:{type:Boolean,default:null},class:{type:null,default:null},style:{type:null,default:null},buttonProps:{type:null,default:null},menuButtonProps:{type:null,default:null},menuButtonIcon:{type:String,default:void 0},dropdownIcon:{type:String,default:void 0},severity:{type:String,default:null},raised:{type:Boolean,default:!1},rounded:{type:Boolean,default:!1},text:{type:Boolean,default:!1},outlined:{type:Boolean,default:!1},size:{type:String,default:null},plain:{type:Boolean,default:!1}},style:Oi,provide:function(){return{$pcSplitButton:this,$parentInstance:this}}},Ze={name:"SplitButton",extends:zi,inheritAttrs:!1,emits:["click"],inject:{$pcFluid:{default:null}},data:function(){return{isExpanded:!1}},mounted:function(){var e=this;this.$watch("$refs.menu.visible",function(o){e.isExpanded=o})},methods:{onDropdownButtonClick:function(e){e&&e.preventDefault(),this.$refs.menu.toggle({currentTarget:this.$el,relatedTarget:this.$refs.button.$el}),this.isExpanded=this.$refs.menu.visible},onDropdownKeydown:function(e){(e.code==="ArrowDown"||e.code==="ArrowUp")&&(this.onDropdownButtonClick(),e.preventDefault())},onDefaultButtonClick:function(e){this.isExpanded&&this.$refs.menu.hide(e),this.$emit("click",e)}},computed:{containerClass:function(){return[this.cx("root"),this.class]},hasFluid:function(){return Se(this.fluid)?!!this.$pcFluid:this.fluid}},components:{PVSButton:R,PVSMenu:Bt,ChevronDownIcon:Wn}},Ki=["data-p-severity"];function Di(r,e,o,u,h,l){var C=re("PVSButton"),$=re("PVSMenu");return m(),b("div",k({class:l.containerClass,style:r.style},r.ptmi("root"),{"data-p-severity":r.severity}),[L(C,k({type:"button",class:r.cx("pcButton"),label:r.label,disabled:r.disabled,severity:r.severity,text:r.text,icon:r.icon,outlined:r.outlined,size:r.size,fluid:r.fluid,"aria-label":r.label,onClick:l.onDefaultButtonClick},r.buttonProps,{pt:r.ptm("pcButton"),unstyled:r.unstyled}),Tt({default:A(function(){return[J(r.$slots,"default")]}),_:2},[r.$slots.icon?{name:"icon",fn:A(function(w){return[J(r.$slots,"icon",{class:Y(w.class)},function(){return[M("span",k({class:[r.icon,w.class]},r.ptm("pcButton").icon,{"data-pc-section":"buttonicon"}),null,16)]})]}),key:"0"}:void 0]),1040,["class","label","disabled","severity","text","icon","outlined","size","fluid","aria-label","onClick","pt","unstyled"]),L(C,k({ref:"button",type:"button",class:r.cx("pcDropdown"),disabled:r.disabled,"aria-haspopup":"true","aria-expanded":h.isExpanded,"aria-controls":h.isExpanded?r.$id+"_overlay":void 0,onClick:l.onDropdownButtonClick,onKeydown:l.onDropdownKeydown,severity:r.severity,text:r.text,outlined:r.outlined,size:r.size,unstyled:r.unstyled},r.menuButtonProps,{pt:r.ptm("pcDropdown")}),{icon:A(function(w){return[J(r.$slots,r.$slots.dropdownicon?"dropdownicon":"menubuttonicon",{class:Y(w.class)},function(){return[(m(),F(Ce(r.menuButtonIcon||r.dropdownIcon?"span":"ChevronDownIcon"),k({class:[r.dropdownIcon||r.menuButtonIcon,w.class]},r.ptm("pcDropdown").icon,{"data-pc-section":"menubuttonicon"}),null,16,["class"]))]})]}),_:3},16,["class","disabled","aria-expanded","aria-controls","onClick","onKeydown","severity","text","outlined","size","unstyled","pt"]),L($,{ref:"menu",id:r.$id+"_overlay",model:r.model,popup:!0,autoZIndex:r.autoZIndex,baseZIndex:r.baseZIndex,appendTo:r.appendTo,unstyled:r.unstyled,pt:r.ptm("pcMenu")},Tt({_:2},[r.$slots.menuitemicon?{name:"itemicon",fn:A(function(w){return[J(r.$slots,"menuitemicon",{item:w.item,class:Y(w.class)})]}),key:"0"}:void 0,r.$slots.item?{name:"item",fn:A(function(w){return[J(r.$slots,"item",{item:w.item,hasSubmenu:w.hasSubmenu,label:w.label,props:w.props})]}),key:"1"}:void 0]),1032,["id","model","autoZIndex","baseZIndex","appendTo","unstyled","pt"])],16,Ki)}Ze.render=Di;const Ri={class:"roles-page"},Bi={key:0,class:"roles-meta-note"},Ui={class:"roles-toolbar"},_i={key:2,class:"roles-active-toggle"},ji={class:"roles-grid-card"},Gi=["src","onClick"],qi=["src","onClick"],Hi={style:{display:"flex",gap:"0.35rem"}},Wi={class:"roles-detail"},Zi={class:"columns-list"},Ji=["for"],Yi={class:"generic-editor-grid"},Qi={key:0},Xi={key:1},er={key:0,class:"generic-editor-tabs"},tr={class:"generic-editor-tab-list"},nr=["onClick"],ir={class:"generic-editor-grid"},rr=["for"],ar={key:4,class:"editor-image-field"},or=["src","onClick"],lr=["for"],sr={key:4,class:"editor-image-field"},ur=["src","onClick"],dr={style:{display:"flex","justify-content":"center","align-items":"center","min-height":"320px"}},cr=["src"],mr={__name:"RolesCrudApp",setup(r){const e=document.getElementById("roles-vue-app"),u=((e?.dataset?.tableName||e?.dataset?.entity||"roles").trim()||"roles").toLowerCase(),h=u==="roles",l=y([]),C=y(!1),$=y(0),w=y(1),c=y(25),x=y(0),V=y("Id"),T=y(1),me=y(""),H=y({}),Ye=y({}),fe=y(!1),pe=y(!1),ae=y(null),Le=y([]),Qe=y([]),oe=y({}),le=y(null),Ut=y(null),Fe=y(null),he=y(!1),be=y(""),B=y(null),ge=y([]),Me=y([]),uo0=y({}),vo0=y({}),Xe=y(!1),et=`crud_${u}_page_size`,tt=`crud_${u}_visible_columns`,_t=`crud_${u}_datatable_state_v1`,nt=[{field:"Id",header:"ID",sortable:!0},{field:"Name",header:"Ruolo",sortable:!0},{field:"LinkedCommand",header:"Comando collegato",sortable:!0},{field:"CreatedAt",header:"Creato",sortable:!0},{field:"UpdatedAt",header:"Aggiornato",sortable:!0}],O=y(h?[...nt]:[]),E=y(O.value.map(t=>t.field)),it=z(()=>{const t=new Map(O.value.map(n=>[n.field,n]));return E.value.map(n=>t.get(n)).filter(Boolean)}),ve=z(()=>!!ae.value),Pe=z(()=>B.value?.allowInsert!==!1),Ve=z(()=>B.value?.allowUpdate!==!1),rt=z(()=>B.value?.allowDelete!==!1),at=z(()=>(Me.value||[]).filter(t=>String(t?.position||"").toLowerCase()==="header")),ot=z(()=>(Me.value||[]).filter(t=>String(t?.position||"").toLowerCase()==="row")),jt=z(()=>{const t=new Map;for(const n of Le.value||[]){const i=String(n?.dependentField||"").toLowerCase();i&&t.set(i,n)}return t}),Gt=z(()=>{const t=new Map;for(const n of Qe.value||[]){const i=String(n?.name||"").toLowerCase();i&&t.set(i,n)}return t}),qt=z(()=>{const t=new Map;for(const n of ge.value||[]){const i=we(n?.fieldName||"");i&&t.set(i,n)}return t}),te=y(!1),se=y("Nuovo ruolo"),U=y(null),W=y({ruolo:"",comandoCollegato:""}),I=y({}),ne=y([]),Te=y(""),Ht=Zn(),lt=z(()=>h?!1:ne.value.some(t=>{const n=ft(t.field);return typeof n=="string"&&n.trim()!==""})),Ne=z(()=>{if(!lt.value)return[];const t=new Map;for(const n of ne.value){const i=ft(n.field),a=String(i||"").trim()||"Generale",s=Jt(a);t.has(s)||t.set(s,{key:s,title:a,fields:[]}),t.get(s).fields.push(n)}return Array.from(t.values())}),st=z(()=>{const t=Ne.value;return t.length?t.some(i=>i.key===Te.value)?Te.value:t[0].key:""}),Q=z(()=>{const t=Array.isArray(l.value)&&l.value.length>0?l.value[0]:null;if(t&&typeof t=="object"){const s=Object.keys(t),p=s.find(d=>d==="Id"||d==="id");if(p)return p;const f=s.find(d=>/id$/i.test(d));if(f)return f}const n=O.value.map(s=>s.field),i=n.find(s=>s==="Id"||s==="id");return i||n.find(s=>/id$/i.test(s))||"Id"});function ut(t,n){try{localStorage.setItem(t,n)}catch{document.cookie=`${t}=${encodeURIComponent(n)}; expires=Fri, 31 Dec 9999 23:59:59 GMT; path=/; SameSite=Lax`}}function dt(t){try{const s=localStorage.getItem(t);if(s!=null)return s}catch{}const n=`${t}=`,a=(document.cookie?document.cookie.split("; "):[]).find(s=>s.startsWith(n));return a?decodeURIComponent(a.substring(n.length)):null}async function ue(t){if(String(t?.headers?.get("content-type")||"").toLowerCase().includes("application/json"))return await t.json();const i=await t.text(),a=String(i||"").trim();if(a.startsWith("{")||a.startsWith("["))try{return JSON.parse(a)}catch{}return{success:t.ok,error:a||null}}function Wt(){const t=parseInt(dt(et)||"",10);!Number.isNaN(t)&&[10,15,25,50,100].includes(t)&&(c.value=t);const n=dt(tt);if(n)try{const i=JSON.parse(n);Array.isArray(i)&&(E.value=i.map(a=>typeof a=="string"?a:null).filter(Boolean))}catch{}}function ye(){if(!E.value.length){const t=O.value[0]?.field||"Id";E.value=[t]}ut(tt,JSON.stringify(E.value))}function ct(t,n){if(pt(n))return"boolean";if(Oe(n))return"number";if($e(n))return"date";if(!Array.isArray(t))return"text";for(const i of t){const a=i?.[n];if(!(a==null||a===""))return typeof a=="boolean"?"boolean":typeof a=="number"?"number":a instanceof Date?"date":"text"}return"text"}function Ae(t,n){return ct(t,n)==="text"?"contains":"equals"}function mt(t){return!t||typeof t!="object"?null:Array.isArray(t.constraints)&&t.constraints.length>0?t.constraints[0]?.value??null:t.value??null}function Ie(t,n,i){return!i||typeof i!="object"?Ae(t,n):Array.isArray(i.constraints)&&i.constraints.length>0?i.constraints[0]?.matchMode||Ae(t,n):i.matchMode||Ae(t,n)}function Zt(t){const n=H.value||{},i={};for(const a of O.value){const s=a.field,p=n[s],f=Ie(t,s,p),d=(p?.operator||"and").toLowerCase()==="or"?"or":"and",S=(Array.isArray(p?.constraints)?p.constraints:[{value:mt(p),matchMode:f}]).map(N=>({value:N?.value??null,matchMode:N?.matchMode||f}));i[s]={operator:d,constraints:S.length>0?S:[{value:null,matchMode:f}]}}H.value=i}function Ee(t){return String(t||"").replace(/_/g," ").replace(/([a-z])([A-Z])/g,"$1 $2").replace(/\s+/g," ").trim().replace(/\b\w/g,n=>n.toUpperCase())}function we(t){return String(t||"").replace(/_/g,"").toLowerCase()}function Jt(t){return String(t).trim().toLowerCase().replace(/\s+/g,"_")}function de(t){return Gt.value.get(we(t))||null}function G(t){return qt.value.get(we(t))||null}function ft(t){const n=G(t),i=n?.groupName??n?.GroupName??null;return typeof i=="string"?i.trim():""}async function Ao0(t){const n=String(B.value?.childTableName||"").trim().toLowerCase(),i=String(B.value?.childTableParentIdFieldName||"").trim();if(!n||!i||t===null||t===void 0||String(t)==="")return;const a=String(t);if(uo0.value[a]||vo0.value[a])return;const Co0=s=>String(s||"").split("_").filter(Boolean).map(p=>p.charAt(0).toUpperCase()+p.slice(1)).join("");const To0=s=>{const p=Co0(s);return p?p.charAt(0).toLowerCase()+p.slice(1):""};vo0.value={...vo0.value,[a]:!0};try{const So0=async s=>{const p={page:1,pageSize:100,sorts:[{field:"Id",dir:"asc"}],filters:[{field:s,op:"eq",value:t}],globalSearch:null},f=await fetch(`/api/crud/${n}/query`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(p)}),d=await ue(f);return{ok:f.ok,res:d}};let {ok:s,res:p}=await So0(i);if(!ok||p?.success===!1){const f=Co0(i);if(f&&f!==i){const d=await So0(f);ok=d.ok;p=d.res}}if(!ok||p?.success===!1){const f=To0(i);if(f&&f!==i){const d=await So0(f);ok=d.ok;p=d.res}}if(!ok||p?.success===!1){const f=String(p?.error||p?.message||"").trim();uo0.value={...uo0.value,[a]:{items:[],error:f||"errore caricamento"}};return}const f=Array.isArray(p?.items)?p.items:[];uo0.value={...uo0.value,[a]:{items:f,error:""}}}catch{uo0.value={...uo0.value,[a]:{items:[],error:"errore caricamento"}}}finally{vo0.value={...vo0.value,[a]:!1}}}function Bo0(t){const n=String(B.value?.childTableName||"").trim(),i=String(B.value?.childTableParentIdFieldName||"").trim();if(!n||!i)return{table:"",parentField:"",parentId:null,loading:!1,error:"",items:[]};const a=t?.[Q.value];if(a===null||a===void 0||String(a)==="")return{table:n,parentField:i,parentId:null,loading:!1,error:"Id parent non disponibile",items:[]};const s=String(a),p=uo0.value[s];if(!p&&!vo0.value[s]){Ao0(a)}if(typeof p==="string")return{table:n,parentField:i,parentId:s,loading:!!vo0.value[s],error:p,items:[]};return{table:n,parentField:i,parentId:s,loading:!!vo0.value[s],error:String(p?.error||"").trim(),items:Array.isArray(p?.items)?p.items:[]}}function Lo0(t){const n=Bo0(t),i=Array.isArray(n.items)?n.items:[];if(i.length<1)return[];const a=i[0]&&typeof i[0]==="object"?Object.keys(i[0]):[];return a.filter(s=>String(s).toLowerCase()!=="rowversion").map(s=>({field:s,header:Ee(s)}))}function Po0(t,n){if(n==null||n==="")return"-";const i=ht(t,n);return i?en(i):String(n)}function Yt(t){Te.value=String(t||"").trim()}function $e(t){const n=de(t),i=String(n?.clrType||"").toLowerCase();return i==="datetime"||i==="datetimeoffset"||i==="dateonly"||i==="timeonly"}function pt(t){const n=String(de(t)?.clrType||"").toLowerCase();return n==="boolean"||n==="bool"}function Oe(t){const n=String(de(t)?.clrType||"").toLowerCase();return["byte","sbyte","int16","uint16","int32","uint32","int64","uint64","single","double","decimal"].includes(n)}function Qt(t){if(h)O.value=[...nt];else{const s=Array.isArray(t)&&t.length>0?t[0]:null;if(s&&typeof s=="object"){const p=Object.keys(s),f=[...p.filter(d=>d.toLowerCase()==="id"),...p.filter(d=>d.toLowerCase()!=="id").filter(d=>!ze(d))];O.value=f.map(d=>({field:d,header:Ee(d),sortable:!0}))}}const n=O.value.find(s=>we(s.field)==="isactive");ae.value=n?.field||null;const i=new Set(O.value.map(s=>s.field)),a=E.value.filter(s=>i.has(s));if(E.value=a.length>0?a:O.value.map(s=>s.field),Array.isArray(ge.value)&&ge.value.length>0){const s=[...O.value].map(d=>{const v=G(d.field);return{...d,header:v?.caption?.trim()?v.caption.trim():d.header,width:Number.isFinite(Number(v?.width))?Number(v.width):null,sortOverride:Number.isFinite(Number(v?.sortOverride))?Number(v.sortOverride):null,visibleOverride:v?.visibleOverride}}).sort((d,v)=>{const S=Number.isFinite(d.sortOverride)?d.sortOverride:Number.MAX_SAFE_INTEGER,N=Number.isFinite(v.sortOverride)?v.sortOverride:Number.MAX_SAFE_INTEGER;return S!==N?S-N:String(d.field).localeCompare(String(v.field))});O.value=s;const p=new Set(s.filter(d=>d.visibleOverride!==!1).map(d=>d.field)),f=E.value.filter(d=>p.has(d));E.value=f.length>0?f:[...p]}Zt(t)}function Xt(t){const n=Number(t?.width);return!Number.isFinite(n)||n<=0?null:`width:${n}px`}function en(t){if(!t)return"-";const n=new Date(t);if(Number.isNaN(n.getTime()))return"-";const i=a=>String(a).padStart(2,"0");return`${i(n.getDate())}/${i(n.getMonth()+1)}/${n.getFullYear()} ${i(n.getHours())}:${i(n.getMinutes())}`}function tn(t){const n=String(t||"").toLowerCase();return n.includes("date")||n.includes("time")||n.includes("created")||n.includes("updated")||n.includes("last")||n.endsWith("at")}function ht(t,n){if(n instanceof Date)return n;if(typeof n!="string")return null;const i=n.trim();if(!i)return null;const a=/^\d{4}-\d{2}-\d{2}(?:[tT ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?/;if(!$e(t)&&!tn(t)&&!a.test(i))return null;const s=new Date(i);return Number.isNaN(s.getTime())?null:s}function bt(t,n){if(wt(n))return kt(t,n);const i=t[n],a=ht(n,i);return a?en(a):i==null||i===""?"-":i}function nn(t){if(typeof t=="boolean")return t;if(typeof t=="number")return t!==0;if(typeof t=="string"){const n=t.trim().toLowerCase();if(n==="true"||n==="1"||n==="yes"||n==="y")return!0;if(n==="false"||n==="0"||n==="no"||n==="n")return!1}return null}function rn(t){if(!ve.value)return null;const n=ae.value;if(!n)return null;const i=nn(t?.[n]);return i===null||i?null:"row-inactive"}function gt(t){return jt.value.get(String(t||"").toLowerCase())||null}function vt(t){const n=String(t||"").toLowerCase();return n==="createdat"||n==="updatedat"||n==="lastupdate"||n==="created_at"||n==="updated_at"||n==="last_update"}function an(t){const n=String(t||"").toLowerCase();return n==="note"||n==="notes"}function yt(t,n=[]){const i=String(t||"").toLowerCase();if(!i.endsWith("description"))return!1;if((Le.value||[]).some(f=>String(f?.descriptionField||"").toLowerCase()===i))return!0;const p=`${String(t||"").slice(0,-11)}Id`.toLowerCase();return n.some(f=>String(f||"").toLowerCase()===p)}function ze(t){const n=String(t||"").toLowerCase();return n==="imagethumbnailurl"||n==="imagepreviewurl"}function on(t){if(t==null||t==="")return"";const n=new Date(t);if(Number.isNaN(n.getTime()))return"";const i=a=>String(a).padStart(2,"0");return`${n.getFullYear()}-${i(n.getMonth()+1)}-${i(n.getDate())}T${i(n.getHours())}:${i(n.getMinutes())}`}function It(t,n){if(gt(t))return"fk";const i=G(t),a=String(i?.editorType||"").toLowerCase();if(a){if(a==="checkbox")return"boolean";if(a==="number")return"number";if(a==="date"||a==="datetime")return"date";if(a==="combo")return"fk";if(a==="textarea"||a==="json")return"note";if(a==="image")return"image";if(a==="readonly")return"text"}return an(t)?"note":pt(t)?"boolean":Oe(t)?"number":$e(t)?"date":typeof n=="boolean"?"boolean":typeof n=="number"?"number":ht(t,n)?"date":"text"}function ln(t){return le.value?String(t||"").toLowerCase()===String(le.value||"").toLowerCase():!1}function sn(t){return String(G(t)?.editorType||"").toLowerCase()==="image"}function wt(t){return Fe.value?String(t||"").toLowerCase()===String(Fe.value||"").toLowerCase():!1}function kt(t,n){const i=Number(t?.[n]),a=Number(t?.UseCount??t?.useCount??0);return Number.isFinite(a)&&a<=0?"":Number.isFinite(i)?`${i.toFixed(2)}%`:"-"}function un(t,n){const i=Number(t?.[n]);return Number.isFinite(i)?i>=85?"usage-rate usage-rate--green":i>=70?"usage-rate usage-rate--yellow":i>=50?"usage-rate usage-rate--orange":"usage-rate usage-rate--red":null}function Ct(t){if(!t||typeof t!="object")return null;const n=t.ImageThumbnailUrl??t.imageThumbnailUrl;return n?String(n):null}function dn(t){if(!t||typeof t!="object")return null;const n=t.ImagePreviewUrl??t.imagePreviewUrl;if(n)return String(n);const i=le.value?t[le.value]:null;return i?String(i):null}function cn(t){const n=dn(t);n&&(be.value=n,he.value=!0)}function X(t){if(t==null)return null;const n=String(t).trim();return n&&(n.startsWith("data:image/")||n.startsWith("http://")||n.startsWith("https://")||n.startsWith("/"))?n:null}function Ke(t){const n=X(t);n&&(be.value=n,he.value=!0)}function St(t){return t.type==="number"||t.type==="boolean"||t.type==="date"?"editor-field--col":"editor-field--full"}function mn(t,n){if(!t||typeof t!="object")return"";const i=t.Name??t.name,a=t.Provider??t.provider;if(i&&a)return`${i} (${a})`;const s=["Description","Name","Title","Code","Label"];for(const f of s){const d=t[f]??t[f.toLowerCase()];if(d!=null&&String(d).trim()!=="")return String(d)}const p=t[n]??t[String(n).toLowerCase()];return p==null?"":String(p)}async function xt(t){const n=t.filter(i=>i.type==="fk");for(const i of n){if(Array.isArray(oe.value[i.field]))continue;const a=gt(i.field),s=String(a?.principalTable||a?.principalEntity||"").trim(),p=String(a?.principalKeyField||"Id").trim();if(s)try{const f=await fetch(`/api/crud/${s}/query`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({page:1,pageSize:1e3,sorts:[{field:p,dir:"asc"}],filters:[],globalSearch:null})}),d=await ue(f);if(!f.ok||d?.success===!1)continue;const S=(Array.isArray(d?.items)?d.items:[]).map(N=>({value:N?.[p],label:mn(N,p)})).filter(N=>N.value!==null&&N.value!==void 0&&String(N.label||"").trim()!=="");oe.value[i.field]=S}catch{oe.value[i.field]=[]}}}function fn(t){return t===-1?"desc":"asc"}function De(t){return String(t?.code||"").trim().toLowerCase()}function Re(t){return String(t?.description||t?.code||"").trim()||"Comando"}function Lt(t){return String(t?.icon||"").trim()||"pi pi-cog"}function Ft(t,n){const i=De(t);if(!i)return!1;if(i.startsWith("models_")){const a=n?.[Q.value];if(a==null)return!1}return!0}function pn(t){return t?.Name??t?.name??t?.Model??t?.model??null}function hn(t,n=null){const i=De(t),a={};if(n&&typeof n=="object"){const s=n?.[Q.value];s!=null&&s!==""&&(a.rowId=Number.isFinite(Number(s))?Number(s):s,a.modelId=Number.isFinite(Number(s))?Number(s):null);const p=pn(n);p&&String(p).trim()!==""&&(a.modelName=String(p).trim())}if(i==="models_run_group"||i==="models_run_all"){const s=window.prompt("Inserisci il gruppo test (vuoto = primo disponibile):","")??"";String(s).trim()!==""&&(a.group=String(s).trim())}return a}async function ke(t,n=null){const i=De(t);if(!i)return;const a=String(t?.confirmMessage||"").trim();if(!(t?.requiresConfirm&&!window.confirm(a||`Confermi comando '${Re(t)}'?`)))try{const s=await fetch(`/api/crud/${u}/commands/${encodeURIComponent(i)}`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(hn(t,n))}),p=await ue(s);if(!s.ok||p?.success===!1)throw new Error(p?.error||"Errore esecuzione comando");const f=p?.runId,d=Array.isArray(p?.runIds)?p.runIds:[],v=f?` (runId: ${f})`:d.length>0?` (runIds: ${d.join(", ")})`:"";alert(`${p?.message||"Comando avviato"}${v}`),await K()}catch(s){alert(s?.message||"Errore inatteso")}}const Mt=z(()=>at.value.map(t=>({label:Re(t),icon:Lt(t),command:()=>ke(t,null)})));function Pt(t){return ot.value.filter(n=>Ft(n,t)).map(n=>({label:Re(n),icon:Lt(n),command:()=>ke(n,t)}))}function bn(){const t=at.value[0];t&&ke(t,null)}function gn(t){const n=ot.value.find(i=>Ft(i,t));n&&ke(n,t)}function vn(){const t=[],n=Object.entries(H.value||{}),i={contains:"contains",startsWith:"startswith",endsWith:"endswith",equals:"eq",notEquals:"neq",lt:"lt",lte:"lte",gt:"gt",gte:"gte"};for(const[a,s]of n){if(pe.value&&ve.value&&String(a).toLowerCase()===String(ae.value).toLowerCase())continue;const p=Array.isArray(s?.constraints)?s.constraints:[{value:mt(s),matchMode:Ie(l.value,a,s)}];for(const f of p){const d=f?.value;if(d==null)continue;const v=ct(l.value,a);let S=d;if(typeof S=="string"&&(S=S.trim()),S==="")continue;if(v==="number"){const _=Number(S);if(Number.isNaN(_))continue;S=_}else if(v==="boolean"){if(typeof S=="string"){const _=S.toLowerCase();_==="true"?S=!0:_==="false"&&(S=!1)}if(typeof S!="boolean")continue}let N=f?.matchMode||Ie(l.value,a,s);v==="text"&&N==="startsWith"&&(N="contains");const Be=i[N]||(v==="text"?"contains":"eq");t.push({field:a,op:Be,value:S})}}return pe.value&&ve.value&&t.push({field:ae.value,op:"eq",value:!0}),t}async function K(){C.value=!0;try{const t={page:w.value,pageSize:c.value,sorts:[{field:V.value,dir:fn(T.value)}],filters:vn(),globalSearch:me.value||null},n=await fetch(`/api/crud/${u}/query`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(t)}),i=await ue(n);if(!n.ok||i?.success===!1)throw new Error(i?.error||"Errore durante il caricamento ruoli");if(Le.value=Array.isArray(i?.foreignKeys)?i.foreignKeys:[],Qe.value=Array.isArray(i?.fields)?i.fields:[],B.value=i?.metadataTable||null,ge.value=Array.isArray(i?.metadataFieldOverrides)?i.metadataFieldOverrides:[],Me.value=Array.isArray(i?.metadataCommands)?i.metadataCommands:[],B.value?.title&&(document.title=B.value.title),le.value=typeof i?.imageField=="string"?i.imageField:null,Ut.value=typeof i?.imageNameField=="string"?i.imageNameField:null,Fe.value=typeof i?.usageStatsField=="string"?i.usageStatsField:null,l.value=i.items||[],$.value=i.totalRows||0,!Xe.value&&B.value){let a=!1;const s=B.value;if(s.defaultPageSize&&Number.isFinite(Number(s.defaultPageSize))){const p=Math.max(1,Number(s.defaultPageSize));c.value!==p&&(c.value=p,x.value=0,w.value=1,a=!0)}if(s.defaultSortField&&String(s.defaultSortField).trim()){const p=String(s.defaultSortField).trim(),f=String(s.defaultSortDirection||"asc").toLowerCase()==="desc"?-1:1;(V.value!==p||T.value!==f)&&(V.value=p,T.value=f,a=!0)}if(Xe.value=!0,a){await K();return}}Qt(l.value),ye()}catch(t){alert(t?.message||"Errore inatteso")}finally{C.value=!1}}function yn(t){x.value=t.first,c.value=t.rows,w.value=Math.floor(t.first/t.rows)+1,ut(et,String(c.value)),K()}function In(t){V.value=t.sortField||"Id",T.value=t.sortOrder||1,K()}function wn(){w.value=1,x.value=0,K()}function kn(t){const n=Array.isArray(t?.columns)?t.columns.map(f=>f?.props?.field).filter(f=>typeof f=="string"&&f.length>0):null;if(n&&n.length>0){const f=new Set(O.value.map(d=>d.field));E.value=n.filter(d=>f.has(d)),ye();return}const i=Number(t?.dragIndex),a=Number(t?.dropIndex);if(!Number.isInteger(i)||!Number.isInteger(a)||i<0||a<0||i>=E.value.length||a>=E.value.length||i===a)return;const s=[...E.value],[p]=s.splice(i,1);s.splice(a,0,p),E.value=s,ye()}function Vt(){w.value=1,x.value=0,K()}function Cn(){me.value="";for(const t of Object.keys(H.value||{})){const n=H.value[t]||{},i=Ie(l.value,t,n);H.value[t]={operator:(n.operator||"and").toLowerCase()==="or"?"or":"and",constraints:[{value:null,matchMode:i}]}}w.value=1,x.value=0,K()}function Sn(){w.value=1,x.value=0,K()}async function xn(){if(Pe.value){if(U.value=null,h)se.value="Nuovo ruolo",W.value={ruolo:"",comandoCollegato:""};else{se.value="Nuovo record";const t=Q.value,n=new Set([t,"RowVersion","rowVersion"]),i=O.value.map(f=>f.field),a=i.filter(f=>!n.has(f)).filter(f=>!ze(f)).filter(f=>!vt(f)).filter(f=>!yt(f,i)).filter(f=>G(f)?.visibleOverride!==!1).filter(f=>G(f)?.readonlyOverride!==!0),s=[],p={};for(const f of a){const d=de(f),v=It(f,null),S=G(f);s.push({field:f,label:S?.caption?.trim()?S.caption.trim():Ee(f),type:v,valueType:v==="number"||v==="fk"?"number":"text",nullable:S?.requiredOverride===!0?!1:d?.nullable!==!1,readonly:S?.readonlyOverride===!0}),p[f]=v==="boolean"?!1:v==="fk"?null:""}ne.value=s,I.value=p,await xt(s)}te.value=!0}}async function Ln(t){if(!Ve.value)return;const n=Q.value;if(U.value=t?.[n]??null,U.value===null||U.value===void 0){alert("Chiave primaria non trovata per il record selezionato.");return}if(h)se.value=`Modifica ruolo #${U.value}`,W.value={ruolo:t.Name||"",comandoCollegato:t.LinkedCommand||""};else{se.value=`Modifica record #${U.value}`;const i=new Set([n,"RowVersion","rowVersion"]),a=Object.keys(t||{}),s=a.filter(d=>!i.has(d)).filter(d=>!ze(d)).filter(d=>!vt(d)).filter(d=>!yt(d,a)).filter(d=>G(d)?.visibleOverride!==!1),p=[],f={};for(const d of s){const v=t[d];if(typeof v=="object"&&v!==null)continue;const S=de(d),N=It(d,v),Be=typeof v=="number"||Oe(d)?"number":"text",_=G(d);p.push({field:d,label:_?.caption?.trim()?_.caption.trim():Ee(d),type:N,valueType:Be,nullable:_?.requiredOverride===!0?!1:S?.nullable??v==null,readonly:_?.readonlyOverride===!0}),N==="date"?f[d]=on(v):f[d]=v??(N==="fk"?null:"")}ne.value=p,I.value=f,await xt(p)}te.value=!0}async function Fn(){if(U.value===null&&!Pe.value||U.value!==null&&!Ve.value)return;let t={};if(h){if(t={Name:W.value.ruolo,LinkedCommand:W.value.comandoCollegato||null},!t.Name){alert("Il campo Ruolo Ã¨ obbligatorio.");return}}else{t={};for(const n of ne.value){if(n.readonly)continue;const i=I.value[n.field];if(n.type==="boolean"){t[n.field]=!!i;continue}if(n.type==="date"){if(i===""||i===null||i===void 0){t[n.field]=n.nullable?null:"";continue}const a=new Date(i);t[n.field]=Number.isNaN(a.getTime())?i:a.toISOString();continue}if(n.type==="fk"){if(i===""||i===null||i===void 0){t[n.field]=n.nullable?null:0;continue}if(n.valueType==="number"){const a=Number(i);if(Number.isNaN(a)){alert(`Valore FK non valido per '${n.label}'.`);return}t[n.field]=a}else t[n.field]=i;continue}if(n.type==="number"){if(i===""||i===null||i===void 0){t[n.field]=n.nullable?null:0;continue}const a=Number(i);if(Number.isNaN(a)){alert(`Valore numerico non valido per '${n.label}'.`);return}t[n.field]=a;continue}i===""||i===void 0?t[n.field]=n.nullable?null:"":t[n.field]=i}}try{const n=U.value!==null,i=n?`/api/crud/${u}/${U.value}`:`/api/crud/${u}`,s=await fetch(i,{method:n?"PUT":"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(t)}),p=await ue(s);if(!s.ok||p?.success===!1)throw new Error(p?.error||"Salvataggio non riuscito");te.value=!1,await K()}catch(n){alert(n?.message||"Errore inatteso")}}function Mn(t,n){const i=n?.Name||n?.Code||n?.Title||n?.[Q.value]||"?";Ht.require({target:t.currentTarget,message:`Eliminare il record '${i}'?`,icon:"pi pi-exclamation-triangle",rejectProps:{label:"No",severity:"secondary",outlined:!0},acceptProps:{label:"Si",severity:"danger"},accept:()=>Pn(n)})}async function Pn(t){if(!rt.value)return;const n=t?.[Q.value];if(n==null){alert("Chiave primaria non trovata per il record selezionato.");return}try{const i=await fetch(`/api/crud/${u}/${n}`,{method:"DELETE"}),a=await ue(i);if(!i.ok||a?.success===!1)throw new Error(a?.error||"Eliminazione non riuscita");await K()}catch(i){alert(i?.message||"Errore inatteso")}}return Tn(()=>{Wt(),K()}),(t,n)=>(m(),b("div",Ri,[B.value?.note?(m(),b("div",Bi,j(B.value.note),1)):P("",!0),M("div",Ui,[Pe.value?(m(),F(g(R),{key:0,label:"Nuovo",icon:"pi pi-plus-circle",onClick:xn})):P("",!0),L(g(R),{label:"Ricarica",icon:"pi pi-sync",severity:"secondary",outlined:"",onClick:K}),L(g(q),{modelValue:me.value,"onUpdate:modelValue":n[0]||(n[0]=i=>me.value=i),modelModifiers:{trim:!0},placeholder:"Filtro globale (tutti i campi)",onKeyup:Nn(Vt,["enter"])},null,8,["modelValue"]),L(g(R),{label:"Filtra",icon:"pi pi-filter",severity:"secondary",onClick:Vt}),L(g(R),{label:"Pulisci filtri",icon:"pi pi-filter-slash",severity:"secondary",outlined:"",onClick:Cn}),L(g(R),{label:"Colonne",icon:"pi pi-table",severity:"secondary",outlined:"",onClick:n[1]||(n[1]=i=>fe.value=!0)}),Mt.value.length>0?(m(),F(g(Ze),{key:1,label:"Comandi",icon:"pi pi-bolt",severity:"secondary",outlined:"",model:Mt.value,onClick:bn},null,8,["model"])):P("",!0),ve.value?(m(),b("div",_i,[n[13]||(n[13]=M("span",{class:"roles-active-toggle-label"},"Solo attivi",-1)),L(g(Dt),{modelValue:pe.value,"onUpdate:modelValue":n[2]||(n[2]=i=>pe.value=i),onChange:Sn},null,8,["modelValue"])])):P("",!0)]),M("div",ji,[L(g(Jn),{value:l.value,filters:H.value,"onUpdate:filters":n[3]||(n[3]=i=>H.value=i),size:"small",scrollable:"",lazy:"",filterDisplay:"menu",reorderableColumns:"",paginator:"",rows:c.value,rowsPerPageOptions:[10,15,25,50,100],first:x.value,totalRecords:$.value,loading:C.value,removableSort:"",dataKey:Q.value,rowClass:rn,stateStorage:"local",stateKey:_t,expandedRows:Ye.value,"onUpdate:expandedRows":n[4]||(n[4]=i=>Ye.value=i),onPage:yn,onSort:In,onFilter:wn,onColumnReorder:kn},{expansion:A(i=>[M("div",Wi,[String(B.value?.childTableName||"").trim()?(m(),b("div",{class:"roles-detail-subtable"},[M("div",{class:"roles-detail-subtable-title"},j(`Sottotabella ${Bo0(i.data).table} (fk ${Bo0(i.data).parentField})`),1),Bo0(i.data).loading?(m(),b("div",{key:0},"Caricamento...")):P("",!0),Bo0(i.data).error?(m(),b("div",{key:1,class:"roles-detail-subtable-error"},j(Bo0(i.data).error),1)):P("",!0),Bo0(i.data).items.length>0?(m(),F(g(Jn),{key:2,value:Bo0(i.data).items,size:"small",scrollable:"",tableStyle:"min-width:36rem"},{default:A(()=>[(m(!0),b(D,null,Z(Lo0(i.data),a=>(m(),F(g(Ge),{key:`sub_${a.field}`,field:a.field,header:a.header,sortable:!0},{body:A(s=>[Nt(j(Po0(a.field,s.data?.[a.field])),1)]),_:2},1032,["field","header"]))),128))]),_:2},1032,["value"])):Bo0(i.data).loading||Bo0(i.data).error?P("",!0):(m(),b("div",{key:3},"Nessun record"))])):(m(!0),b(D,null,Z(it.value,a=>(m(),b("div",{key:`exp_${a.field}`,class:"roles-detail-item"},[M("span",{class:"roles-detail-key"},j(a.header),1),M("span",{class:"roles-detail-value"},j(bt(i.data,a.field)),1)]))),128))])]),default:A(()=>[L(g(Ge),{expander:"",reorderableColumn:!1,frozen:"",alignFrozen:"left",style:{width:"3rem"}}),(m(!0),b(D,null,Z(it.value,i=>(m(),F(g(Ge),{key:i.field,field:i.field,header:i.header,sortable:i.sortable,style:An(Xt(i)),filter:!0,showFilterMenu:!0,showFilterOperator:!0,showAddButton:!0,reorderableColumn:!0},{body:A(a=>[ln(i.field)&&Ct(a.data)?(m(),b("img",{key:0,src:Ct(a.data),alt:"thumbnail",style:{width:"50px",height:"50px","object-fit":"cover",cursor:"pointer","border-radius":"4px",border:"1px solid #d1d5db"},onClick:s=>cn(a.data)},null,8,Gi)):sn(i.field)&&X(a.data?.[i.field])?(m(),b("img",{key:1,src:X(a.data?.[i.field]),alt:"thumbnail",style:{width:"50px",height:"50px","object-fit":"cover",cursor:"pointer","border-radius":"4px",border:"1px solid #d1d5db"},onClick:s=>Ke(a.data?.[i.field])},null,8,qi)):wt(i.field)?(m(),b("span",{key:2,class:Y(un(a.data,i.field))},j(kt(a.data,i.field)),3)):(m(),b(D,{key:3},[Nt(j(bt(a.data,i.field)),1)],64))]),_:2},1032,["field","header","sortable","style"]))),128)),L(g(Ge),{reorderableColumn:!1,header:"Azioni",style:{width:"12rem"}},{body:A(i=>[M("div",Hi,[Ve.value?(m(),F(g(R),{key:0,icon:"pi pi-pencil",size:"small",outlined:"","aria-label":"Modifica",onClick:a=>Ln(i.data)},null,8,["onClick"])):P("",!0),rt.value?(m(),F(g(R),{key:1,icon:"pi pi-trash",size:"small",severity:"danger",outlined:"","aria-label":"Elimina",onClick:a=>Mn(a,i.data)},null,8,["onClick"])):P("",!0),Pt(i.data).length>0?(m(),F(g(Ze),{key:2,label:"Comandi",icon:"pi pi-cog",size:"small",severity:"secondary",outlined:"",model:Pt(i.data),onClick:a=>gn(i.data)},null,8,["model","onClick"])):P("",!0)])]),_:1})]),_:1},8,["value","filters","rows","first","totalRecords","loading","dataKey","expandedRows"])]),L(g(He),{visible:fe.value,"onUpdate:visible":n[7]||(n[7]=i=>fe.value=i),header:"Colonne visibili",modal:"",style:{width:"26rem"}},{footer:A(()=>[L(g(R),{label:"Chiudi",severity:"secondary",text:"",onClick:n[6]||(n[6]=i=>fe.value=!1)})]),default:A(()=>[M("div",Zi,[(m(!0),b(D,null,Z(O.value,i=>(m(),b("div",{key:i.field,class:"columns-item"},[L(g(qe),{modelValue:E.value,"onUpdate:modelValue":n[5]||(n[5]=a=>E.value=a),inputId:`col_${i.field}`,value:i.field,onChange:ye},null,8,["modelValue","inputId","value"]),M("label",{for:`col_${i.field}`},j(i.header),9,Ji)]))),128))])]),_:1},8,["visible"]),L(g(He),{visible:te.value,"onUpdate:visible":n[11]||(n[11]=i=>te.value=i),header:se.value,modal:"",class:"generic-editor-dialog",style:{width:"min(96vw, 72rem)"}},{footer:A(()=>[L(g(R),{label:"Annulla",severity:"secondary",text:"",onClick:n[10]||(n[10]=i=>te.value=!1)}),L(g(R),{label:"Salva",icon:"pi pi-check",onClick:Fn})]),default:A(()=>[M("div",Yi,[h?(m(),b("div",Qi,[n[14]||(n[14]=M("label",{for:"ruolo",style:{display:"block","font-weight":"600","margin-bottom":"0.3rem"}},"Ruolo",-1)),L(g(q),{id:"ruolo",modelValue:W.value.ruolo,"onUpdate:modelValue":n[8]||(n[8]=i=>W.value.ruolo=i),modelModifiers:{trim:!0},style:{width:"100%"}},null,8,["modelValue"])])):P("",!0),h?(m(),b("div",Xi,[n[15]||(n[15]=M("label",{for:"comando",style:{display:"block","font-weight":"600","margin-bottom":"0.3rem"}},"Comando collegato",-1)),L(g(q),{id:"comando",modelValue:W.value.comandoCollegato,"onUpdate:modelValue":n[9]||(n[9]=i=>W.value.comandoCollegato=i),modelModifiers:{trim:!0},style:{width:"100%"}},null,8,["modelValue"])])):P("",!0),h?P("",!0):(m(),b(D,{key:2},[lt.value?(m(),b("div",er,[M("div",tr,[(m(!0),b(D,null,Z(Ne.value,i=>(m(),b("button",{key:`tab_${i.key}`,type:"button",class:Y(["generic-editor-tab",{"generic-editor-tab--active":st.value===i.key}]),onClick:a=>Yt(i.key)},j(i.title),11,nr))),128))]),(m(!0),b(D,null,Z(Ne.value,i=>$t((m(),b("div",{key:`panel_${i.key}`,class:"generic-editor-tab-panel"},[M("div",ir,[(m(!0),b(D,null,Z(i.fields,a=>(m(),b("div",{key:a.field,class:Y(["editor-field",St(a)])},[M("label",{for:`field_${a.field}`,style:{display:"block","font-weight":"600","margin-bottom":"0.3rem"}},j(a.label),9,rr),a.type==="boolean"?(m(),F(g(qe),{key:0,inputId:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,binary:"",disabled:a.readonly},null,8,["inputId","modelValue","onUpdate:modelValue","disabled"])):a.type==="fk"?(m(),F(g(Et),{key:1,id:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,options:oe.value[a.field]||[],optionLabel:"label",optionValue:"value",showClear:"",filter:"",placeholder:"Seleziona...",style:{width:"100%"},disabled:a.readonly},null,8,["id","modelValue","onUpdate:modelValue","options","disabled"])):a.type==="date"?(m(),F(g(q),{key:2,id:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,type:"datetime-local",style:{width:"100%"},disabled:a.readonly},null,8,["id","modelValue","onUpdate:modelValue","disabled"])):a.type==="note"?(m(),F(g(We),{key:3,id:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,rows:"3",autoResize:"",style:{width:"100%"},disabled:a.readonly},null,8,["id","modelValue","onUpdate:modelValue","disabled"])):a.type==="image"?(m(),b("div",ar,[L(g(q),{id:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,type:"text",style:{width:"100%"},disabled:a.readonly,placeholder:"URL o path immagine"},null,8,["id","modelValue","onUpdate:modelValue","disabled"]),X(I.value[a.field])?(m(),b("img",{key:0,src:X(I.value[a.field]),alt:"preview",class:"editor-image-thumb",onClick:s=>Ke(I.value[a.field])},null,8,or)):P("",!0)])):(m(),F(g(q),{key:5,id:`field_${a.field}`,modelValue:I.value[a.field],"onUpdate:modelValue":s=>I.value[a.field]=s,type:a.type==="number"?"number":"text",style:{width:"100%"},disabled:a.readonly},null,8,["id","modelValue","onUpdate:modelValue","type","disabled"]))],2))),128))])])),[[En,st.value===i.key]])),128))])):(m(!0),b(D,{key:1},Z(ne.value,i=>(m(),b("div",{key:i.field,class:Y(["editor-field",St(i)])},[M("label",{for:`field_${i.field}`,style:{display:"block","font-weight":"600","margin-bottom":"0.3rem"}},j(i.label),9,lr),i.type==="boolean"?(m(),F(g(qe),{key:0,inputId:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,binary:"",disabled:i.readonly},null,8,["inputId","modelValue","onUpdate:modelValue","disabled"])):i.type==="fk"?(m(),F(g(Et),{key:1,id:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,options:oe.value[i.field]||[],optionLabel:"label",optionValue:"value",showClear:"",filter:"",placeholder:"Seleziona...",style:{width:"100%"},disabled:i.readonly},null,8,["id","modelValue","onUpdate:modelValue","options","disabled"])):i.type==="date"?(m(),F(g(q),{key:2,id:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,type:"datetime-local",style:{width:"100%"},disabled:i.readonly},null,8,["id","modelValue","onUpdate:modelValue","disabled"])):i.type==="note"?(m(),F(g(We),{key:3,id:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,rows:"3",autoResize:"",style:{width:"100%"},disabled:i.readonly},null,8,["id","modelValue","onUpdate:modelValue","disabled"])):i.type==="image"?(m(),b("div",sr,[L(g(q),{id:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,type:"text",style:{width:"100%"},disabled:i.readonly,placeholder:"URL o path immagine"},null,8,["id","modelValue","onUpdate:modelValue","disabled"]),X(I.value[i.field])?(m(),b("img",{key:0,src:X(I.value[i.field]),alt:"preview",class:"editor-image-thumb",onClick:a=>Ke(I.value[i.field])},null,8,ur)):P("",!0)])):(m(),F(g(q),{key:5,id:`field_${i.field}`,modelValue:I.value[i.field],"onUpdate:modelValue":a=>I.value[i.field]=a,type:i.type==="number"?"number":"text",style:{width:"100%"},disabled:i.readonly},null,8,["id","modelValue","onUpdate:modelValue","type","disabled"]))],2))),128))],64))])]),_:1},8,["visible","header"]),L(g(He),{visible:he.value,"onUpdate:visible":n[12]||(n[12]=i=>he.value=i),header:"Anteprima immagine",modal:"",style:{width:"min(96vw, 980px)"}},{default:A(()=>[M("div",dr,[be.value?(m(),b("img",{key:0,src:be.value,alt:"preview",style:{"max-width":"100%","max-height":"75vh","object-fit":"contain"}},null,8,cr)):P("",!0)])]),_:1},8,["visible"]),L(g(Yn))]))}};$n(mr).use(Qn,{theme:{preset:Xn,options:{darkModeSelector:!1}}}).use(ei).mount("#roles-vue-app");





