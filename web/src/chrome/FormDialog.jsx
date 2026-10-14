import { Button, Dialog, FormError, FormScope } from "@thekrystalship/krystal";

// FormDialog — a dialog that is itself the form of a `useForm` handle: Enter submits, Cancel closes,
// and `submit` is the foot's primary button. A refusal stands in the body as `notice`; without one the
// form's own error (`FormError`) does.
function FormDialog({ form, name, title, icon, sub, onClose, submit, cancel = "Cancel", notice, children }) {
  return (
    <FormScope form={form}>
      <Dialog title={title} icon={icon} sub={sub} as="form" onSubmit={form.submit}
        onClose={onClose} canClose={!form.isSubmitting}
        foot={(
          <>
            <Button variant="outline" onClick={onClose} disabled={form.isSubmitting}>{cancel}</Button>
            {submit}
          </>
        )}>
        <div className="k-form" data-dialog={name}>
          {notice === undefined ? <FormError /> : notice}
          {children}
        </div>
      </Dialog>
    </FormScope>
  );
}

export { FormDialog };
