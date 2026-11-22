window.quantum = window.quantum || {};
window.quantum.operationToast = {
  registrations: new Map(),

  show(id, element, dotNetReference, noticeId, isError) {
    this.dispose(id);
    const state = {
      element,
      remaining: isError ? 8000 : 4000,
      started: 0,
      timer: null,
      fading: false,
      hovered: element.matches(':hover'),
      focused: element.contains(document.activeElement),
    };
    const isCurrent = () => this.registrations.get(id) === state;
    const schedule = () => {
      if (!isCurrent() || state.hovered || state.focused) {
        return;
      }
      state.started = performance.now();
      state.timer = setTimeout(() => {
        if (!isCurrent()) {
          return;
        }
        state.remaining = 0;
        state.fading = true;
        element.classList.add('fading');
        state.timer = setTimeout(() => {
          if (isCurrent()) {
            state.timer = null;
            dotNetReference.invokeMethodAsync('DismissAsync', noticeId).catch(() => {});
          }
        }, 180);
      }, state.remaining);
    };
    const pause = () => {
      clearTimeout(state.timer);
      state.timer = null;
      if (!state.fading) {
        state.remaining = Math.max(0, state.remaining - (performance.now() - state.started));
      }
      state.fading = false;
      element.classList.remove('fading');
    };
    state.enter = () => {
      if (!state.hovered && !state.focused) {
        pause();
      }
      state.hovered = true;
    };
    state.leave = () => {
      state.hovered = false;
      if (!state.focused) {
        schedule();
      }
    };
    state.focusIn = () => {
      if (!state.hovered && !state.focused) {
        pause();
      }
      state.focused = true;
    };
    state.focusOut = (event) => {
      if (element.contains(event.relatedTarget)) {
        return;
      }
      state.focused = false;
      if (!state.hovered) {
        schedule();
      }
    };
    element.classList.remove('fading');
    element.addEventListener('pointerenter', state.enter);
    element.addEventListener('pointerleave', state.leave);
    element.addEventListener('focusin', state.focusIn);
    element.addEventListener('focusout', state.focusOut);
    this.registrations.set(id, state);
    schedule();
  },

  dispose(id) {
    const state = this.registrations.get(id);
    if (!state) {
      return;
    }
    clearTimeout(state.timer);
    state.element.classList.remove('fading');
    state.element.removeEventListener('pointerenter', state.enter);
    state.element.removeEventListener('pointerleave', state.leave);
    state.element.removeEventListener('focusin', state.focusIn);
    state.element.removeEventListener('focusout', state.focusOut);
    this.registrations.delete(id);
  },
};
