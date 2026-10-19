# deploy transaction spike 2026-10-18 20:20:06Z
endpoint: powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg
sample:   C:\Users\BorisKamberGarcevic\repos\tomix-cli\.claude\worktrees\bosteder-rls-validation-53c9ef\samples\deploy-qa

## A0 explicit transaction on an idle session
- BeginTransaction: ok (37 ms)
- RollbackTransaction: ok (38 ms)
- BeginTransaction (2): ok (33 ms)
- CommitTransaction (empty): ok (36 ms)

## A1 new database 'txspike-new-1018202006': deploy broken model in a transaction, inspect, roll back
- BeginTransaction: ok (45 ms)
- Execute createOrReplace (broken): ok (11443 ms)
  inspect [mid-transaction, after createOrReplace]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=8
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- Execute refresh calculate: ok (958 ms)
  inspect [mid-transaction, after refresh calculate]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=4
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- RollbackTransaction: ok (62 ms)
  inspect [after rollback, fresh connection (expect: not found)]
    attempt 1: database not listed yet
    attempt 2: database not listed yet
    attempt 3: database not listed yet
    attempt 4: database not listed yet
    attempt 5: database not listed yet
    attempt 6: database not listed yet
    database not found

## A2 existing database 'txspike-existing-1018202006': good model committed, then broken model in a transaction, roll back
- Execute createOrReplace (good, autocommit): ok (12043 ms)
  inspect [baseline after good deploy]
    Sales[Total Amount] expression=`SUM ( Sales[Amount] )` state=Ready error=
    TomDeployValidator errors=0 warnings=0 unprocessed=8
- BeginTransaction: ok (38 ms)
- Execute createOrReplace (broken): ok (1900 ms)
  inspect [mid-transaction, after createOrReplace]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=8
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- Execute refresh calculate: ok (1112 ms)
  inspect [mid-transaction, after refresh calculate]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=4
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- RollbackTransaction: ok (55 ms)
  inspect [after rollback, fresh connection (expect: good expression, no errors)]
    attempt 1: database not listed yet
    attempt 2: database not listed yet
    Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    TomDeployValidator errors=1 warnings=0 unprocessed=4

## A4 'txspike-existing-1018202006': restore good model, then begin, deploy broken, roll back immediately
- Execute createOrReplace (good, autocommit): ok (1650 ms)
- BeginTransaction: ok (43 ms)
- Execute createOrReplace (broken): ok (1617 ms)
- RollbackTransaction: ok (52 ms)
  inspect [after rollback, fresh connection (expect: good expression, no errors)]
    Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    TomDeployValidator errors=1 warnings=0 unprocessed=7

## A5 'txspike-existing-1018202006': restore good model, then begin, alter one measure via TOM SaveChanges, inspect, roll back
- Execute createOrReplace (good, autocommit): ok (1500 ms)
- database visible: True (databases in workspace: 22)
    'txspike-new-1018201822' id=a35eaee7-6431-4276-bf73-eaede398b52b
    'txspike-existing-1018201822' id=ce0e76d6-a3c2-4884-85c5-cec60215b0bb
    'txspike-new-1018202006' id=248fa0b9-4450-4c23-90a8-4ca7e5f1d326
    'txspike-existing-1018202006' id=db7eee46-19f0-451c-89f7-5e8ca7b79e8b
- BeginTransaction: ok (37 ms)
- SaveChanges (measure -> SUM ( Sales[Nope] )): ok (101 ms)
  inspect [mid-transaction, after SaveChanges]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=7
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- RollbackTransaction: ok (79 ms)
  inspect [after rollback, fresh connection (expect: good expression, no errors)]
    attempt 1: database not listed yet
    Sales[Total Amount] expression=`SUM ( Sales[Amount] )` state=Ready error=
    TomDeployValidator errors=0 warnings=0 unprocessed=7

## A6 'txspike-existing-1018202006': restore + process good model, then begin, CopyTo(broken model), SaveChanges, inspect, roll back
- Execute createOrReplace (good, autocommit): ok (1685 ms)
- Execute refresh full (autocommit): ok (2814 ms)
  rows [baseline]: Customers=2 Sales=1
- BeginTransaction: ok (37 ms)
    SaveChanges xmla results: 1
- CopyTo (broken) + SaveChanges: ok (140 ms)
  inspect [mid-transaction, after SaveChanges]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=3
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- RollbackTransaction (errors=1): ok (81 ms)
  inspect [after rollback, fresh connection (expect: good expression, no errors)]
    Sales[Total Amount] expression=`SUM ( Sales[Amount] )` state=Ready error=
    TomDeployValidator errors=0 warnings=0 unprocessed=0
  rows [after rollback (expect: baseline)]: Customers=2 Sales=1

## A7 'txspike-existing-1018202006': begin, CopyTo(good model + changed description), SaveChanges, inspect, commit
- BeginTransaction: ok (34 ms)
    SaveChanges xmla results: 1
- CopyTo (good + description) + SaveChanges: ok (96 ms)
  inspect [mid-transaction, after SaveChanges]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Amount] )` state=Ready error=
    AMO: TomDeployValidator errors=0 warnings=0 unprocessed=3
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Amount] )` state=1 error=
- CommitTransaction: ok (657 ms)
  inspect [after commit, fresh connection (expect: no errors)]
    Sales[Total Amount] expression=`SUM ( Sales[Amount] )` state=Ready error=
    TomDeployValidator errors=0 warnings=0 unprocessed=3
  rows [after commit (expect: baseline, data kept)]: Customers=2 Sales=1

## A3 control on 'txspike-existing-1018202006': broken model in a transaction, commit
- BeginTransaction: ok (31 ms)
- Execute createOrReplace (broken): ok (1758 ms)
  inspect [mid-transaction, after createOrReplace]
    AMO: Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    AMO: TomDeployValidator errors=1 warnings=0 unprocessed=5
      Measure Sales[Total Amount] SemanticError: Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    DMV: Customer Count expression=`COUNTROWS ( Customers )` state=1 error=
    DMV: Total Amount expression=`SUM ( Sales[Nope] )` state=5 error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
- CommitTransaction: FAILED (55 ms) OperationException: The commit failed for this transaction. Error returned: 'Bekræftelses- eller tilbagerulningsanmodningen kan ikke fuldføres, fordi der ikke er en aktiv transaktion i denne session.  Technical Details: RootActivityId: 9b889e1b-a8b1-4a20-89c4-61338a093e1c Date (UTC): 10/18/2026 8:21:48 PM '.
  inspect [after commit, fresh connection (expect: SemanticError on Sales[Total Amount])]
    Sales[Total Amount] expression=`SUM ( Sales[Nope] )` state=SemanticError error=Kolonnen 'Nope' i tabellen 'Sales' blev ikke fundet, eller den kan muligvis ikke bruges i dette udtryk.
    TomDeployValidator errors=1 warnings=0 unprocessed=5

## cleanup
- delete 'txspike-new-1018202006': ok (1625 ms)
- delete 'txspike-existing-1018202006': ok (1601 ms)
